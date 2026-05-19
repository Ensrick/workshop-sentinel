using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public class RefreshExecutorTests
{
    [Fact]
    public void MutateAcf_flips_sentinels_in_both_subtrees_and_sets_top_level_NeedsDownload()
    {
        var src = """
            "AppWorkshop"
            {
                "appid" "552500"
                "NeedsDownload" "0"
                "WorkshopItemsInstalled"
                {
                    "1" { "size" "100" "timeupdated" "1700000001" "manifest" "aaaa" }
                    "2" { "size" "200" "timeupdated" "1700000002" "manifest" "bbbb" }
                    "3" { "size" "300" "timeupdated" "1700000003" "manifest" "cccc" }
                }
                "WorkshopItemDetails"
                {
                    "1" { "manifest" "aaaa" "timeupdated" "1700000001" }
                    "2" { "manifest" "bbbb" "timeupdated" "1700000002" }
                    "3" { "manifest" "cccc" "timeupdated" "1700000003" }
                }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-sentinel-test-{Path.GetRandomFileName()}.acf");
        File.WriteAllText(tmp, src);
        try
        {
            var mutated = RefreshExecutor.MutateAcf(tmp, new ulong[] { 1, 3, 99 });
            Assert.Equal(new ulong[] { 1, 3 }, mutated.OrderBy(x => x).ToArray());

            var reparsed = AcfNode.Parse(File.ReadAllText(tmp));

            // Top-level NeedsDownload bumped — the proven trigger.
            Assert.Equal("1", reparsed["NeedsDownload"]!.AsString());

            // Item 1: sentinels in both subtrees.
            Assert.Equal("1",  reparsed["WorkshopItemsInstalled"]!["1"]!["timeupdated"]!.AsString());
            Assert.Equal("-1", reparsed["WorkshopItemsInstalled"]!["1"]!["manifest"]!.AsString());
            Assert.Equal("1",  reparsed["WorkshopItemDetails"]!["1"]!["timeupdated"]!.AsString());
            Assert.Equal("-1", reparsed["WorkshopItemDetails"]!["1"]!["manifest"]!.AsString());

            // Item 2 untouched.
            Assert.Equal("1700000002", reparsed["WorkshopItemsInstalled"]!["2"]!["timeupdated"]!.AsString());
            Assert.Equal("bbbb",       reparsed["WorkshopItemsInstalled"]!["2"]!["manifest"]!.AsString());

            // Item 3: sentinels written.
            Assert.Equal("1",  reparsed["WorkshopItemsInstalled"]!["3"]!["timeupdated"]!.AsString());
            Assert.Equal("-1", reparsed["WorkshopItemsInstalled"]!["3"]!["manifest"]!.AsString());

            // size + ugchandle (had we set them) are preserved — we only flip the two stale fields.
            Assert.Equal("100", reparsed["WorkshopItemsInstalled"]!["1"]!["size"]!.AsString());
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void MutateAcf_noop_when_no_ids_match()
    {
        var src = """
            "AppWorkshop"
            {
                "WorkshopItemsInstalled" { "1" { "size" "100" "timeupdated" "1700000001" "manifest" "a" } }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-sentinel-test-{Path.GetRandomFileName()}.acf");
        File.WriteAllText(tmp, src);
        var beforeText = File.ReadAllText(tmp);
        try
        {
            var mutated = RefreshExecutor.MutateAcf(tmp, new ulong[] { 999 });
            Assert.Empty(mutated);
            Assert.Equal(beforeText, File.ReadAllText(tmp)); // unchanged on disk
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task RefreshAsync_groups_by_appid_writes_snapshot_and_mutates_acf()
    {
        // Two items for the same appid + one for a different appid → expect two
        // ACFs mutated, two .bak snapshots, three steam:// emissions (belt-and-suspenders),
        // and content folders untouched.
        using var tempRoot = new TempLibrary();

        tempRoot.WriteAcf(552500, ItemsInstalledFor(new ulong[] { 100, 200 }));
        tempRoot.WriteAcf(730,    ItemsInstalledFor(new ulong[] { 999 }));
        tempRoot.MakeContentDir(552500, 100);
        tempRoot.MakeContentDir(552500, 200);
        tempRoot.MakeContentDir(730,    999);

        var emittedUrls = new List<string>();
        var executor = new RefreshExecutor(new[] { tempRoot.RootPath }, url =>
        {
            emittedUrls.Add(url);
            return Task.CompletedTask;
        });

        var items = new[]
        {
            new WorkshopItemLocal(552500, 100, 10, "a", 100),
            new WorkshopItemLocal(552500, 200, 20, "b", 200),
            new WorkshopItemLocal(730,    999, 30, "c", 300),
        };
        // verifyTimeout=Zero — verify-phase exercised by dedicated tests.
        var outcomes = await executor.RefreshAsync(items, verifyTimeout: TimeSpan.Zero);

        Assert.All(outcomes, o => Assert.True(o.Success, o.Error));
        Assert.Equal(3, emittedUrls.Count);
        Assert.Contains("steam://workshop_download_item/552500/100", emittedUrls);
        Assert.Contains("steam://workshop_download_item/552500/200", emittedUrls);
        Assert.Contains("steam://workshop_download_item/730/999",    emittedUrls);

        // CRITICAL: content folders MUST stay put. This is the data-loss bug fix.
        Assert.True(Directory.Exists(tempRoot.ContentDir(552500, 100)));
        Assert.True(Directory.Exists(tempRoot.ContentDir(552500, 200)));
        Assert.True(Directory.Exists(tempRoot.ContentDir(730,    999)));

        // .bak snapshot exists alongside each mutated ACF.
        Assert.True(File.Exists(tempRoot.AcfPath(552500) + ".workshop-sentinel.bak"));
        Assert.True(File.Exists(tempRoot.AcfPath(730)    + ".workshop-sentinel.bak"));

        // Both ACFs should have those items' sentinels written and NeedsDownload=1.
        var acf552500 = AcfNode.ParseFile(tempRoot.AcfPath(552500));
        Assert.Equal("1", acf552500["NeedsDownload"]!.AsString());
        Assert.Equal("1",  acf552500["WorkshopItemsInstalled"]!["100"]!["timeupdated"]!.AsString());
        Assert.Equal("-1", acf552500["WorkshopItemsInstalled"]!["100"]!["manifest"]!.AsString());
        Assert.Equal("1",  acf552500["WorkshopItemsInstalled"]!["200"]!["timeupdated"]!.AsString());

        var acf730 = AcfNode.ParseFile(tempRoot.AcfPath(730));
        Assert.Equal("1", acf730["WorkshopItemsInstalled"]!["999"]!["timeupdated"]!.AsString());
    }

    [Fact]
    public async Task RefreshAsync_reports_error_when_acf_is_missing()
    {
        using var tempRoot = new TempLibrary();
        var executor = new RefreshExecutor(new[] { tempRoot.RootPath }, _ => Task.CompletedTask);

        var outcomes = await executor.RefreshAsync(new[]
        {
            new WorkshopItemLocal(11111, 42, 0, "", 0),
        }, verifyTimeout: TimeSpan.Zero);

        Assert.Single(outcomes);
        Assert.False(outcomes[0].Success);
        Assert.Contains("not found", outcomes[0].Error ?? "");
    }

    [Fact]
    public async Task RefreshAsync_verify_emits_VerifyFailed_when_steam_never_flips_timeupdated()
    {
        // Steam ignored the nudge — timeupdated stays at the sentinel "1" forever.
        // Phase 4 must surface this as a hard failure so the user knows the re-pull
        // didn't land. The .bak is intact, content is intact, recovery is trivial.
        using var tempRoot = new TempLibrary();
        tempRoot.WriteAcf(552500, ItemsInstalledFor(new ulong[] { 100 }));
        tempRoot.MakeContentDir(552500, 100);
        var executor = new RefreshExecutor(new[] { tempRoot.RootPath }, _ => Task.CompletedTask);
        var steps = new List<RefreshStep>();
        var progress = new Progress<RefreshStep>(steps.Add);

        var outcomes = await executor.RefreshAsync(
            new[] { new WorkshopItemLocal(552500, 100, 10, "a", 100) },
            progress,
            verifyTimeout: TimeSpan.FromMilliseconds(300),
            readItemTimeUpdated: (_, _) => 1L);  // Steam never moved off the sentinel

        await Task.Delay(50);

        Assert.Single(outcomes);
        Assert.False(outcomes[0].Success);
        Assert.Contains("Verification timeout", outcomes[0].Error ?? "");
        Assert.Contains(steps, s => s.Stage == "VerifyStart"  && s.PublishedFileId == 100);
        Assert.Contains(steps, s => s.Stage == "VerifyFailed" && s.PublishedFileId == 100);

        // Content folder MUST still exist after a failed verify — recovery path.
        Assert.True(Directory.Exists(tempRoot.ContentDir(552500, 100)));
    }

    [Fact]
    public async Task RefreshAsync_verify_emits_VerifyOk_when_steam_flips_timeupdated_to_real_epoch()
    {
        // Happy path: Steam picks up the ACF mutation and writes a fresh download
        // timestamp into the per-item entry within a few seconds. Phase 4 sees it.
        using var tempRoot = new TempLibrary();
        tempRoot.WriteAcf(552500, ItemsInstalledFor(new ulong[] { 100 }));
        tempRoot.MakeContentDir(552500, 100);
        var executor = new RefreshExecutor(new[] { tempRoot.RootPath }, _ => Task.CompletedTask);
        var steps = new List<RefreshStep>();
        var progress = new Progress<RefreshStep>(steps.Add);

        // Real epoch ~2026-05-19 — well above MinRealEpoch threshold.
        var realEpoch = 1747680000L;
        var outcomes = await executor.RefreshAsync(
            new[] { new WorkshopItemLocal(552500, 100, 10, "a", 100) },
            progress,
            verifyTimeout: TimeSpan.FromSeconds(2),
            readItemTimeUpdated: (_, _) => realEpoch);

        await Task.Delay(50);

        Assert.Single(outcomes);
        Assert.True(outcomes[0].Success, outcomes[0].Error);
        Assert.Contains(steps, s => s.Stage == "VerifyStart" && s.PublishedFileId == 100);
        Assert.Contains(steps, s => s.Stage == "VerifyOk"    && s.PublishedFileId == 100);
        Assert.DoesNotContain(steps, s => s.Stage == "VerifyFailed");
    }

    private static string ItemsInstalledFor(IEnumerable<ulong> ids)
    {
        // Pre-mutation timestamps are real epochs (~2023) so they don't trip the
        // "sentinel still present" check before we run.
        var installed = string.Join("\n            ", ids.Select(id =>
            $"\"{id}\" {{ \"size\" \"{id}\" \"timeupdated\" \"1700000000\" \"manifest\" \"m{id}\" }}"));
        var details = string.Join("\n            ", ids.Select(id =>
            $"\"{id}\" {{ \"manifest\" \"m{id}\" \"timeupdated\" \"1700000000\" }}"));
        return $$"""
            "AppWorkshop"
            {
                "NeedsDownload" "0"
                "WorkshopItemsInstalled"
                {
                    {{installed}}
                }
                "WorkshopItemDetails"
                {
                    {{details}}
                }
            }
            """;
    }

    /// <summary>Temp library root with auto-cleanup. Layout mirrors a real Steam library.</summary>
    private sealed class TempLibrary : System.IDisposable
    {
        public string RootPath { get; }
        public TempLibrary()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"ws-sentinel-lib-{Path.GetRandomFileName()}");
            Directory.CreateDirectory(Path.Combine(RootPath, "steamapps", "workshop", "content"));
        }
        public string AcfPath(uint appId) => Path.Combine(RootPath, "steamapps", "workshop", $"appworkshop_{appId}.acf");
        public string ContentDir(uint appId, ulong itemId) => Path.Combine(RootPath, "steamapps", "workshop", "content", appId.ToString(), itemId.ToString());

        public void WriteAcf(uint appId, string contents)
            => File.WriteAllText(AcfPath(appId), contents);

        public void MakeContentDir(uint appId, ulong itemId)
        {
            var dir = ContentDir(appId, itemId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker"), "x");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
