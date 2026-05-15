using System.Collections.Generic;
using System.Linq;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class WorkshopEnumeratorTests
{
    [Fact]
    public void Parses_three_items_from_a_single_acf()
    {
        var content = """
            "AppWorkshop"
            {
                "appid"     "552500"
                "WorkshopItemsInstalled"
                {
                    "100" { "size" "1234"  "timeupdated" "1700000000"  "manifest" "aaa" }
                    "200" { "size" "5678"  "timeupdated" "1700000001"  "manifest" "bbb" }
                    "300" { "size" "0"     "timeupdated" "1700000002"  "manifest" "ccc" }
                }
            }
            """;
        var enumerator = new WorkshopEnumerator();

        var items = enumerator
            .EnumerateFromMemory(new Dictionary<string, string>
            {
                ["appworkshop_552500.acf"] = content,
            })
            .OrderBy(i => i.PublishedFileId)
            .ToList();

        Assert.Equal(3, items.Count);
        Assert.Equal(552500u,     items[0].AppId);
        Assert.Equal(100ul,       items[0].PublishedFileId);
        Assert.Equal(1234L,       items[0].LocalSizeBytes);
        Assert.Equal(1700000000L, items[0].LocalTimeUpdated);
        Assert.Equal("aaa",       items[0].LocalManifest);
    }

    [Fact]
    public void Filename_drives_appid_not_internal_value()
    {
        // Steam's own files always agree internal-vs-filename, but the filename is the
        // authoritative source per Steam Support — internal `appid` is informational.
        var content = """
            "AppWorkshop" {
                "appid" "999999"
                "WorkshopItemsInstalled" { "1" { "size" "1" "timeupdated" "1" "manifest" "x" } }
            }
            """;
        var enumerator = new WorkshopEnumerator();

        var items = enumerator
            .EnumerateFromMemory(new Dictionary<string, string>
            {
                ["appworkshop_552500.acf"] = content,
            })
            .ToList();

        Assert.Single(items);
        Assert.Equal(552500u, items[0].AppId);
    }

    [Fact]
    public void Multiple_acf_files_yield_combined_results()
    {
        var enumerator = new WorkshopEnumerator();

        var items = enumerator
            .EnumerateFromMemory(new Dictionary<string, string>
            {
                ["appworkshop_111.acf"] =
                    """ "AppWorkshop" { "WorkshopItemsInstalled" { "1" { "size" "1" "timeupdated" "1" "manifest" "a" } } } """,
                ["appworkshop_222.acf"] =
                    """ "AppWorkshop" { "WorkshopItemsInstalled" { "2" { "size" "2" "timeupdated" "2" "manifest" "b" } } } """,
            })
            .OrderBy(i => i.AppId)
            .ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(111u, items[0].AppId);
        Assert.Equal(222u, items[1].AppId);
    }

    [Fact]
    public void Corrupt_file_is_skipped_silently()
    {
        var enumerator = new WorkshopEnumerator();

        var items = enumerator
            .EnumerateFromMemory(new Dictionary<string, string>
            {
                ["appworkshop_111.acf"] = "this is not acf at all",
                ["appworkshop_222.acf"] =
                    """ "AppWorkshop" { "WorkshopItemsInstalled" { "2" { "size" "2" "timeupdated" "2" "manifest" "b" } } } """,
            })
            .ToList();

        Assert.Single(items);
        Assert.Equal(222u, items[0].AppId);
    }

    [Fact]
    public void Missing_WorkshopItemsInstalled_block_yields_zero_items()
    {
        var enumerator = new WorkshopEnumerator();

        var items = enumerator
            .EnumerateFromMemory(new Dictionary<string, string>
            {
                ["appworkshop_552500.acf"] =
                    """ "AppWorkshop" { "appid" "552500" "SizeOnDisk" "0" } """,
            })
            .ToList();

        Assert.Empty(items);
    }

    [Fact]
    public void Files_not_matching_acf_naming_pattern_are_ignored()
    {
        var enumerator = new WorkshopEnumerator();

        var items = enumerator
            .EnumerateFromMemory(new Dictionary<string, string>
            {
                ["appmanifest_552500.acf"] =
                    """ "AppWorkshop" { "WorkshopItemsInstalled" { "1" { "size" "1" "timeupdated" "1" "manifest" "a" } } } """,
                ["random.txt"] = "whatever",
            })
            .ToList();

        Assert.Empty(items);
    }
}
