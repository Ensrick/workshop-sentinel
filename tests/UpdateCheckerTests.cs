using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class UpdateCheckerTests
{
    private const string SampleReleaseJson = """
        {
          "tag_name": "v0.3.1",
          "assets": [
            {
              "name": "WorkshopSentinel.exe",
              "browser_download_url": "https://example.com/WorkshopSentinel.exe",
              "size": 12345,
              "digest": "sha256:ABC123"
            }
          ]
        }
        """;

    [Theory]
    [InlineData("0.3.0", "0.3.1", UpdateStatus.UpdateAvailable)]
    [InlineData("0.3.1", "0.3.1", UpdateStatus.Latest)]
    [InlineData("0.3.2", "0.3.1", UpdateStatus.Latest)]
    [InlineData("0.3.0-alpha", "0.3.0", UpdateStatus.UpdateAvailable)]
    [InlineData("0.3.0", "0.3.0", UpdateStatus.Latest)]
    public void CompareStatus_handles_semver_and_prerelease(string current, string latest, UpdateStatus expected)
    {
        Assert.Equal(expected, UpdateChecker.CompareStatus(current, latest));
    }

    [Fact]
    public void ParseLatestRelease_extracts_tag_url_size_digest()
    {
        var result = UpdateChecker.ParseLatestRelease(SampleReleaseJson, "WorkshopSentinel.exe");
        Assert.NotNull(result);
        Assert.Equal("0.3.1",                                          result!.LatestVersion);
        Assert.Equal("https://example.com/WorkshopSentinel.exe",       result.DownloadUrl);
        Assert.Equal(12345L,                                           result.AssetSize);
        Assert.Equal("abc123",                                         result.AssetSha256);
    }

    [Fact]
    public async Task CheckAsync_uses_fresh_cache_when_current_version_matches()
    {
        // Cache was written by THIS install (CurrentVersion=0.3.1) within the TTL.
        // The HTTP handler MUST NOT be called — that would defeat the cache.
        using var tmpDir = new TempDir();
        var cachePath = tmpDir.PathFor("cache.json");
        WriteCache(cachePath, new UpdateCheckResult(
            UpdateStatus.Latest, "0.3.1", "0.3.1", "https://example.com/exe",
            100L, "deadbeef", null, DateTime.UtcNow.AddMinutes(-5)));

        var handler = new StubHandler(_ => throw new InvalidOperationException("should not hit GitHub"));
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, cachePath: cachePath, releasesUrl: "https://example.com/releases");
        var result = await checker.CheckAsync("0.3.1");

        Assert.Equal(UpdateStatus.Latest, result.Status);
        Assert.Equal("0.3.1",             result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_repolls_when_running_version_differs_from_cached_current()
    {
        // BUG SCENARIO: cache was written by v0.2.1 (CurrentVersion="0.2.1"), found 0.3.0 as
        // latest, user installed it → now running 0.3.0. Old check: RecomputeStatus would
        // say "you're on 0.3.0, latest is 0.3.0, no update" — even if 0.3.1 has since
        // shipped. The fix: when cached.CurrentVersion doesn't match the running binary,
        // force a fresh GitHub poll. Burned in v0.3.0 → v0.3.1.
        using var tmpDir = new TempDir();
        var cachePath = tmpDir.PathFor("cache.json");
        WriteCache(cachePath, new UpdateCheckResult(
            UpdateStatus.UpdateAvailable, "0.2.1", "0.3.0",
            "https://example.com/v030", 100L, "deadbeef", null,
            DateTime.UtcNow.AddMinutes(-5)));   // well within the 6h TTL

        var hits = 0;
        var handler = new StubHandler(_ =>
        {
            hits++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(SampleReleaseJson) };
        });
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, cachePath: cachePath, releasesUrl: "https://example.com/releases");
        var result = await checker.CheckAsync("0.3.0");   // running 0.3.0 now, cache says we were 0.2.1

        Assert.Equal(1, hits);                              // cache invalidated → fresh poll
        Assert.Equal("0.3.1", result.LatestVersion);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public async Task CheckAsync_repolls_when_cache_is_stale()
    {
        // Same as the cache-fresh test but the cache timestamp is older than TTL.
        using var tmpDir = new TempDir();
        var cachePath = tmpDir.PathFor("cache.json");
        WriteCache(cachePath, new UpdateCheckResult(
            UpdateStatus.Latest, "0.3.1", "0.3.1", null, null, null, null,
            DateTime.UtcNow - TimeSpan.FromHours(7)));   // past 6h TTL

        var hits = 0;
        var handler = new StubHandler(_ =>
        {
            hits++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(SampleReleaseJson) };
        });
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, cachePath: cachePath, releasesUrl: "https://example.com/releases");
        _ = await checker.CheckAsync("0.3.1");

        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task CheckAsync_forceRefresh_bypasses_fresh_cache()
    {
        using var tmpDir = new TempDir();
        var cachePath = tmpDir.PathFor("cache.json");
        WriteCache(cachePath, new UpdateCheckResult(
            UpdateStatus.Latest, "0.3.1", "0.3.1", null, null, null, null,
            DateTime.UtcNow));

        var hits = 0;
        var handler = new StubHandler(_ =>
        {
            hits++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(SampleReleaseJson) };
        });
        using var http = new HttpClient(handler);
        var checker = new UpdateChecker(http, cachePath: cachePath, releasesUrl: "https://example.com/releases");
        _ = await checker.CheckAsync("0.3.1", forceRefresh: true);

        Assert.Equal(1, hits);
    }

    private static void WriteCache(string path, UpdateCheckResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { _respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_respond(req));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"ws-sentinel-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string PathFor(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
