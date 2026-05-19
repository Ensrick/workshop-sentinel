using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WorkshopSentinel.Services;

public enum SubscribeOutcome
{
    Ok,
    AuthFailed,         // 401/403 — cookies stale or wrong account
    ServerRejected,     // 200 OK but body says { "success": <non-1> } — Steam said no (banned item, removed, etc.)
    NetworkError,
    MalformedResponse,  // 200 OK but no parseable JSON
}

public sealed record SubscribeResult(
    SubscribeOutcome Outcome,
    bool             Success,
    string?          ErrorDetail);

/// <summary>
/// POSTs to <c>steamcommunity.com/sharedfiles/subscribe</c> with the live Steam session
/// cookies, subscribing the logged-in account to a Workshop item. Verified working
/// 2026-05-17 against the live Steam community endpoint (PC-B). The server-side
/// subscription is immediate; the Steam client's local <c>appworkshop_&lt;appid&gt;.acf</c>
/// updates on the next client-side sync (Steam restart, or ~10 min sub-monitor tick).
///
/// Friends-only items subscribe fine via this path as long as the consumer account is
/// friends with the owner — authentication is by Steam JWT in <c>steamLoginSecure</c>,
/// not by Web API key, so visibility-on-friend rules apply normally.
/// </summary>
public sealed class SteamSubscribeClient
{
    private const string Endpoint = "https://steamcommunity.com/sharedfiles/subscribe";

    private readonly HttpClient _http;
    private readonly SteamSessionCookies _cookies;

    public SteamSubscribeClient(HttpClient http, SteamSessionCookies cookies)
    {
        _http    = http;
        _cookies = cookies;
    }

    /// <summary>
    /// Subscribe the current account to a single published Workshop item.
    /// </summary>
    public async Task<SubscribeResult> SubscribeAsync(
        uint appId, ulong publishedFileId, CancellationToken ct = default)
    {
        var body = $"id={publishedFileId}&appid={appId}&sessionid={Uri.EscapeDataString(_cookies.SessionId)}";

        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.TryAddWithoutValidation("Origin",            "https://steamcommunity.com");
        req.Headers.TryAddWithoutValidation("Referer",           "https://steamcommunity.com/");
        req.Headers.TryAddWithoutValidation("X-Requested-With",  "XMLHttpRequest");
        // Per-request cookie header — bypasses HttpClient's CookieContainer, which would
        // otherwise need an extra HttpClientHandler dance. The two cookies are all we need.
        req.Headers.TryAddWithoutValidation("Cookie",
            $"sessionid={_cookies.SessionId}; steamLoginSecure={_cookies.SteamLoginSecure}");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return new SubscribeResult(SubscribeOutcome.NetworkError, false, ex.Message);
        }
        catch (TaskCanceledException)
        {
            return new SubscribeResult(SubscribeOutcome.NetworkError, false, "timeout");
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized ||
                resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return new SubscribeResult(SubscribeOutcome.AuthFailed, false,
                    $"HTTP {(int)resp.StatusCode} — Steam rejected the cookies. " +
                    "Open Steam, ensure you're logged in, visit any Workshop page in the overlay, then retry.");
            }
            if (!resp.IsSuccessStatusCode)
            {
                return new SubscribeResult(SubscribeOutcome.NetworkError, false,
                    $"HTTP {(int)resp.StatusCode}");
            }

            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseResponse(payload);
        }
    }

    /// <summary>
    /// Parse Steam's response body. Verified live: success is <c>{"success":1}</c>;
    /// any other shape is treated as a server-side rejection or malformed reply.
    /// Exposed for unit tests.
    /// </summary>
    public static SubscribeResult ParseResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new SubscribeResult(SubscribeOutcome.MalformedResponse, false, "empty body");
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var successEl))
            {
                var success = successEl.ValueKind switch
                {
                    JsonValueKind.Number => successEl.GetInt32(),
                    JsonValueKind.String => int.TryParse(successEl.GetString(), out var v) ? v : -1,
                    _                    => -1,
                };
                if (success == 1)
                    return new SubscribeResult(SubscribeOutcome.Ok, true, null);
                return new SubscribeResult(SubscribeOutcome.ServerRejected, false,
                    $"Steam returned success={success} (item may be banned, removed, or you lack visibility).");
            }
            return new SubscribeResult(SubscribeOutcome.MalformedResponse, false,
                $"response has no 'success' field: {Trim(body)}");
        }
        catch (JsonException)
        {
            return new SubscribeResult(SubscribeOutcome.MalformedResponse, false,
                $"non-JSON response: {Trim(body)}");
        }
    }

    private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;
}
