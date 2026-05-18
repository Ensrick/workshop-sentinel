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
    public void RewriteAcf_removes_targeted_ids_from_both_subtrees()
    {
        var src = """
            "AppWorkshop"
            {
                "appid" "552500"
                "WorkshopItemsInstalled"
                {
                    "1" { "size" "100" "timeupdated" "10" "manifest" "a" }
                    "2" { "size" "200" "timeupdated" "20" "manifest" "b" }
                    "3" { "size" "300" "timeupdated" "30" "manifest" "c" }
                }
                "WorkshopItemDetails"
                {
                    "1" { "manifest" "a" }
                    "2" { "manifest" "b" }
                    "3" { "manifest" "c" }
                }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-sentinel-test-{Path.GetRandomFileName()}.acf");
        File.WriteAllText(tmp, src);
        try
        {
            var removed = RefreshExecutor.RewriteAcf(tmp, new ulong[] { 1, 3, 99 });
            Assert.Equal(new ulong[] { 1, 3 }, removed.OrderBy(x => x).ToArray());

            var reparsed = AcfNode.Parse(File.ReadAllText(tmp));
            Assert.Null(reparsed["WorkshopItemsInstalled"]!["1"]);
            Assert.NotNull(reparsed["WorkshopItemsInstalled"]!["2"]);
            Assert.Null(reparsed["WorkshopItemsInstalled"]!["3"]);
            Assert.Null(reparsed["WorkshopItemDetails"]!["1"]);
            Assert.NotNull(reparsed["WorkshopItemDetails"]!["2"]);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void RewriteAcf_noop_when_no_ids_match()
    {
        var src = """
            "AppWorkshop"
            {
                "WorkshopItemsInstalled" { "1" { "size" "100" "timeupdated" "10" "manifest" "a" } }
            }
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-sentinel-test-{Path.GetRandomFileName()}.acf");
        File.WriteAllText(tmp, src);
        var beforeText = File.ReadAllText(tmp);
        try
        {
            var removed = RefreshExecutor.RewriteAcf(tmp, new ulong[] { 999 });
            Assert.Empty(removed);
            Assert.Equal(beforeText, File.ReadAllText(tmp)); // unchanged on disk
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public async Task RefreshAsync_groups_by_appid_and_calls_steam_url_per_item()
    {
        // Two items for the same appid + one for a different appid → expect two ACF rewrites,
        // three steam:// emissions (one per item).
        using var tempRoot = new TempLibrary();

        // Library root layout: <root>/steamapps/workshop/{appworkshop_X.acf, content/X/Y/}
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
        var outcomes = await executor.RefreshAsync(items);

        Assert.All(outcomes, o => Assert.True(o.Success, o.Error));
        Assert.Equal(3, emittedUrls.Count);
        Assert.Contains("steam://workshop_download_item/552500/100", emittedUrls);
        Assert.Contains("steam://workshop_download_item/552500/200", emittedUrls);
        Assert.Contains("steam://workshop_download_item/730/999",    emittedUrls);

        Assert.False(Directory.Exists(tempRoot.ContentDir(552500, 100)));
        Assert.False(Directory.Exists(tempRoot.ContentDir(552500, 200)));
        Assert.False(Directory.Exists(tempRoot.ContentDir(730,    999)));

        // Both ACFs should have those items stripped.
        var acf552500 = AcfNode.ParseFile(tempRoot.AcfPath(552500));
        Assert.Null(acf552500["WorkshopItemsInstalled"]!["100"]);
        Assert.Null(acf552500["WorkshopItemsInstalled"]!["200"]);
        var acf730 = AcfNode.ParseFile(tempRoot.AcfPath(730));
        Assert.Null(acf730["WorkshopItemsInstalled"]!["999"]);
    }

    [Fact]
    public async Task RefreshAsync_reports_error_when_acf_is_missing()
    {
        using var tempRoot = new TempLibrary();
        var executor = new RefreshExecutor(new[] { tempRoot.RootPath }, _ => Task.CompletedTask);

        var outcomes = await executor.RefreshAsync(new[]
        {
            new WorkshopItemLocal(11111, 42, 0, "", 0),
        });

        Assert.Single(outcomes);
        Assert.False(outcomes[0].Success);
        Assert.Contains("not found", outcomes[0].Error ?? "");
    }

    private static string ItemsInstalledFor(IEnumerable<ulong> ids)
    {
        var entries = string.Join("\n        ", ids.Select(id => $"\"{id}\" {{ \"size\" \"{id}\" \"timeupdated\" \"{id}\" \"manifest\" \"m{id}\" }}"));
        return $$"""
            "AppWorkshop"
            {
                "WorkshopItemsInstalled"
                {
                    {{entries}}
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
