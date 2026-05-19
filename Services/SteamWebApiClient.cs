using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WorkshopSentinel.Services;

/// <summary>
/// Wraps the Steam Web API endpoint
/// <c>ISteamRemoteStorage/GetPublishedFileDetails/v1/</c>. No API key required for public
/// items; private/friends-only items return <c>result=9</c> with no fields, surfaced here
/// as a <see cref="WorkshopItemRemote"/> with <c>ApiResult=9</c> and every other field null.
///
/// Batches 100 IDs per request, retries 429/5xx with exponential backoff (3 attempts).
/// Caller passes the HttpClient so tests can inject a mock handler.
/// </summary>
public sealed class SteamWebApiClient
{
    public const int BatchSize = 100;
    private const string Endpoint = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    private readonly HttpClient _http;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly int _maxAttempts;

    public SteamWebApiClient(HttpClient http, int maxAttempts = 3, Func<int, TimeSpan>? backoff = null)
    {
        _http = http;
        _maxAttempts = maxAttempts;
        _backoff = backoff ?? DefaultBackoff;
    }

    private static TimeSpan DefaultBackoff(int attempt)
        => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));

    public async Task<Dictionary<ulong, WorkshopItemRemote>> GetPublishedFileDetailsAsync(
        IEnumerable<ulong> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.Distinct().ToList();
        var result = new Dictionary<ulong, WorkshopItemRemote>(idList.Count);

        for (int i = 0; i < idList.Count; i += BatchSize)
        {
            var batch = idList.Skip(i).Take(BatchSize).ToList();
            var batchResult = await FetchBatchWithRetryAsync(batch, cancellationToken).ConfigureAwait(false);
            foreach (var (id, item) in batchResult) result[id] = item;
        }

        return result;
    }

    private async Task<Dictionary<ulong, WorkshopItemRemote>> FetchBatchWithRetryAsync(
        List<ulong> batch, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                using var response = await SendBatchAsync(batch, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return ParseResponse(json, batch);
                }
                if (!ShouldRetry(response.StatusCode) || attempt == _maxAttempts)
                {
                    return MarkAllFailed(batch);
                }
            }
            catch (HttpRequestException) when (attempt < _maxAttempts) { /* fall through to backoff */ }
            catch (HttpRequestException) { return MarkAllFailed(batch); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < _maxAttempts) { /* timeout — retry */ }

            await Task.Delay(_backoff(attempt), ct).ConfigureAwait(false);
        }
        return MarkAllFailed(batch);
    }

    private static bool ShouldRetry(HttpStatusCode code) =>
        code == HttpStatusCode.TooManyRequests || (int)code >= 500;

    private Task<HttpResponseMessage> SendBatchAsync(List<ulong> batch, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>(batch.Count + 1)
        {
            new("itemcount", batch.Count.ToString(CultureInfo.InvariantCulture)),
        };
        for (int i = 0; i < batch.Count; i++)
        {
            form.Add(new($"publishedfileids[{i}]", batch[i].ToString(CultureInfo.InvariantCulture)));
        }
        return _http.PostAsync(Endpoint, new FormUrlEncodedContent(form), ct);
    }

    private static Dictionary<ulong, WorkshopItemRemote> MarkAllFailed(IEnumerable<ulong> batch)
    {
        var dict = new Dictionary<ulong, WorkshopItemRemote>();
        foreach (var id in batch)
            dict[id] = new WorkshopItemRemote(id, null, null, null, null, null, ApiResult: -1);
        return dict;
    }

    /// <summary>
    /// Filter a sub-list down to "real mods for the given app." Steam's
    /// <c>myworkshopfiles?appid=N</c> URL filter is loose, so controller configs
    /// (file_type 13), cross-game items (consumer_app_id mismatch), and localization-key
    /// placeholders (title starts with `#Library_*` etc.) leak in. This is the canonical
    /// "is this a mod the user actually wants to see?" predicate.
    /// </summary>
    public static bool IsMod(WorkshopItemRemote remote, uint expectedAppId)
    {
        if (remote.ApiResult != 1) return false;
        if (remote.FileType is not null && remote.FileType != 0 && remote.FileType != 7) return false;
        if (remote.ConsumerAppId is not null && remote.ConsumerAppId != expectedAppId) return false;
        if (!string.IsNullOrEmpty(remote.Title) && remote.Title.StartsWith('#')) return false;
        return true;
    }

    /// <summary>
    /// Should this row appear in the matrix? Differs from <see cref="IsMod"/> in one
    /// important way: when the no-key API returned <c>result=9</c> (friends-only) or
    /// <c>result=-1</c> (network failure) we have no metadata to judge by, so we err on
    /// the side of *showing* the row. The friend's public sub-page already told us the
    /// ID exists; the user is right to expect it in the grid. Audit columns will show
    /// "—" and the title falls back to "#&lt;id&gt;", but the friend column still
    /// correctly shows ✓.
    /// <para>For result=1 (full metadata), we delegate to <see cref="IsMod"/> so controller
    /// configs, cross-game leaks, and <c>#Library_*</c> placeholders still get stripped.</para>
    /// </summary>
    public static bool ShouldDisplay(WorkshopItemRemote remote, uint expectedAppId)
    {
        if (remote.ApiResult != 1) return true;
        return IsMod(remote, expectedAppId);
    }

    // ---------- JSON shape ----------
    //
    // Confirmed live against ct (3712929235, public) and wt (3712896117, friends-only):
    //
    // public item:
    //   { "response": {
    //       "result": 1, "resultcount": 1,
    //       "publishedfiledetails": [{
    //         "publishedfileid": "3712929235",
    //         "result": 1,
    //         "creator": "76561198211120891",
    //         "file_size": "1399903",
    //         "title": "...", "description": "...",
    //         "time_created": 1776993035, "time_updated": 1778811142,
    //         "visibility": 0, "banned": 0, ...
    //       }]
    //   }}
    //
    // friends-only / private item:
    //   { "response": {
    //       "result": 1, "resultcount": 1,
    //       "publishedfiledetails": [{ "publishedfileid": "...", "result": 9 }]
    //   }}
    //
    // Note: `publishedfileid` and `file_size` arrive as JSON strings (Steam's convention for
    // large unsigned ints). Numeric fields like `time_updated` arrive as numbers.
    public static Dictionary<ulong, WorkshopItemRemote> ParseResponse(string json, IEnumerable<ulong> requested)
    {
        var result = new Dictionary<ulong, WorkshopItemRemote>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var resp) ||
                !resp.TryGetProperty("publishedfiledetails", out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return MarkAllFailed(requested);
            }

            foreach (var d in details.EnumerateArray())
            {
                if (!TryReadULong(d, "publishedfileid", out var id)) continue;
                var apiResult = d.TryGetProperty("result", out var r) && r.TryGetInt32(out var rv) ? rv : -1;

                if (apiResult != 1)
                {
                    result[id] = new WorkshopItemRemote(id, null, null, null, null, null, apiResult);
                    continue;
                }

                var title       = d.TryGetProperty("title", out var t) ? t.GetString() : null;
                var fileSize    = TryReadLongLoose(d, "file_size");
                var timeUpdated = TryReadLongLoose(d, "time_updated");
                var visibility  = d.TryGetProperty("visibility", out var v) && v.TryGetInt32(out var vv) ? vv : (int?)null;
                bool? banned    = d.TryGetProperty("banned",     out var b) && b.TryGetInt32(out var bv) ? bv != 0 : null;
                var fileType    = d.TryGetProperty("file_type",        out var ft) && ft.TryGetInt32(out var ftv)  ? ftv  : (int?)null;
                var consumerApp = d.TryGetProperty("consumer_app_id",  out var ca) && ca.TryGetUInt32(out var cav) ? cav  : (uint?)null;

                result[id] = new WorkshopItemRemote(
                    id, title, timeUpdated, fileSize, visibility, banned, apiResult,
                    FileType: fileType, ConsumerAppId: consumerApp);
            }
        }
        catch (JsonException)
        {
            return MarkAllFailed(requested);
        }

        // Fill in any requested IDs the server didn't return.
        foreach (var requestedId in requested)
        {
            if (!result.ContainsKey(requestedId))
                result[requestedId] = new WorkshopItemRemote(requestedId, null, null, null, null, null, ApiResult: -1);
        }
        return result;
    }

    private static bool TryReadULong(JsonElement elem, string name, out ulong value)
    {
        value = 0;
        if (!elem.TryGetProperty(name, out var p)) return false;
        return p.ValueKind switch
        {
            JsonValueKind.String => ulong.TryParse(p.GetString(), out value),
            JsonValueKind.Number => p.TryGetUInt64(out value),
            _ => false,
        };
    }

    // Steam mixes numeric strings and JSON numbers for size-like fields — accept both.
    private static long? TryReadLongLoose(JsonElement elem, string name)
    {
        if (!elem.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(p.GetString(), out var s) ? s : (long?)null,
            JsonValueKind.Number => p.TryGetInt64(out var n) ? n : (long?)null,
            _ => null,
        };
    }
}
