using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WorkshopSentinel.Services;

public enum UpdateStatus
{
    Latest,
    UpdateAvailable,
    CheckFailed,
}

/// <summary>
/// One snapshot of an update-availability check. Persisted to disk as JSON so we can
/// short-circuit subsequent launches inside the cache TTL.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? DownloadUrl,
    long? AssetSize,
    string? AssetSha256,
    string? ErrorMessage,
    DateTime CheckedAtUtc);

/// <summary>
/// Polls the GitHub Releases API for the latest WorkshopSentinel release and compares the
/// tag against the running version. Disk-caches the last successful result for 6h so we
/// don't hammer the API on every launch. Failed checks aren't cached — next launch retries.
///
/// Asset selection: looks for a release asset named exactly <c>WorkshopSentinel.exe</c>.
/// Reads <c>tag_name</c> (strips a leading 'v'), <c>browser_download_url</c>, <c>size</c>,
/// and <c>digest</c> (GitHub returns <c>sha256:&lt;hex&gt;</c> for assets with digests).
/// </summary>
public sealed class UpdateChecker
{
    public const string DefaultReleasesUrl =
        "https://api.github.com/repos/Ensrick/workshop-sentinel/releases/latest";

    public const string DefaultAssetName = "WorkshopSentinel.exe";

    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _releasesUrl;
    private readonly string _assetName;

    public UpdateChecker(
        HttpClient http,
        string? cachePath = null,
        string? releasesUrl = null,
        string? assetName = null)
    {
        _http = http;
        _cachePath = cachePath ?? DefaultCachePath();
        _releasesUrl = releasesUrl ?? DefaultReleasesUrl;
        _assetName = assetName ?? DefaultAssetName;
    }

    public static string DefaultCachePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "WorkshopSentinel", "update-cache.json");
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh)
        {
            var cached = TryLoadCache();
            if (cached is not null && DateTime.UtcNow - cached.CheckedAtUtc < CacheTtl)
            {
                // Cached result was computed against whatever version was running back then —
                // re-evaluate against the version we're running *now* so a just-installed
                // exe doesn't keep seeing "update available" for the rest of the TTL window.
                return RecomputeStatus(cached, currentVersion);
            }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Failed(currentVersion, $"HTTP {(int)resp.StatusCode}");
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var parsed = ParseLatestRelease(json, _assetName);
            if (parsed is null)
            {
                return Failed(currentVersion, $"release payload missing '{_assetName}' asset");
            }

            var result = parsed with
            {
                CurrentVersion = currentVersion,
                Status = CompareStatus(currentVersion, parsed.LatestVersion!),
                CheckedAtUtc = DateTime.UtcNow,
            };

            TrySaveCache(result);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Failed(currentVersion, ex.Message);
        }
    }

    private static UpdateCheckResult Failed(string current, string msg) =>
        new(UpdateStatus.CheckFailed, current, null, null, null, null, msg, DateTime.UtcNow);

    /// <summary>
    /// Parses one GitHub <c>/releases/latest</c> payload. Returns null if no asset named
    /// <paramref name="assetName"/> is present. The <c>Status</c> and <c>CurrentVersion</c>
    /// fields on the returned record are placeholders — the caller fills those in.
    /// </summary>
    public static UpdateCheckResult? ParseLatestRelease(string json, string assetName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
        var tag = tagEl.GetString();
        if (string.IsNullOrEmpty(tag)) return null;
        var version = tag.TrimStart('v', 'V');

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            long? size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : null;
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            // GitHub returns "sha256:<hex>" — strip the prefix so callers can hex-compare.
            string? sha256 = !string.IsNullOrEmpty(digest)
                && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest.Substring(7).ToLowerInvariant()
                    : null;

            return new UpdateCheckResult(
                UpdateStatus.UpdateAvailable,
                CurrentVersion: "",
                LatestVersion: version,
                DownloadUrl: url,
                AssetSize: size,
                AssetSha256: sha256,
                ErrorMessage: null,
                CheckedAtUtc: DateTime.UtcNow);
        }
        return null;
    }

    /// <summary>
    /// semver-ish compare: split each side on '-' first, parse the numeric prefix with
    /// <see cref="Version.TryParse(string?, out Version)"/>. If the numeric prefixes tie,
    /// a pre-release suffix (e.g. <c>-alpha</c>) on the running version with a clean tag on
    /// the latest counts as "update available" — that's how we get folks off the alpha.
    /// </summary>
    public static UpdateStatus CompareStatus(string current, string latest)
    {
        if (!TryParseSemverPrefix(current, out var cv)) return UpdateStatus.CheckFailed;
        if (!TryParseSemverPrefix(latest,  out var lv)) return UpdateStatus.CheckFailed;
        if (lv > cv) return UpdateStatus.UpdateAvailable;
        if (lv == cv && current.Contains('-') && !latest.Contains('-')) return UpdateStatus.UpdateAvailable;
        return UpdateStatus.Latest;
    }

    private static bool TryParseSemverPrefix(string s, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(s)) return false;
        var prefix = s.Split('-', 2)[0];
        return Version.TryParse(prefix, out version!);
    }

    // ---- cache ----
    private UpdateCheckResult? TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOpts);
        }
        catch { return null; }
    }

    private void TrySaveCache(UpdateCheckResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(result, JsonOpts));
        }
        catch { /* best effort — cache is a perf optimization, not load-bearing */ }
    }

    private static UpdateCheckResult RecomputeStatus(UpdateCheckResult cached, string currentVersion)
    {
        if (string.IsNullOrEmpty(cached.LatestVersion)) return cached with { CurrentVersion = currentVersion };
        return cached with
        {
            CurrentVersion = currentVersion,
            Status = CompareStatus(currentVersion, cached.LatestVersion),
        };
    }
}
