using System;
using System.IO;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class AcfParserTests
{
    [Fact]
    public void Empty_input_throws()
    {
        Assert.Throws<FormatException>(() => AcfParser_Parse(""));
        Assert.Throws<FormatException>(() => AcfParser_Parse("   \n  \t  "));
    }

    [Fact]
    public void Minimal_wrapper_with_one_pair()
    {
        var node = AcfParser_Parse("""
            "Wrap"
            {
                "k"     "v"
            }
            """);

        Assert.Equal("v", node["k"]!.AsString());
    }

    [Fact]
    public void Nested_object()
    {
        var node = AcfParser_Parse("""
            "Wrap"
            {
                "outer"
                {
                    "inner_a"   "1"
                    "inner_b"   "2"
                    "deep"
                    {
                        "leaf"  "true"
                    }
                }
            }
            """);

        Assert.Equal(1L, node["outer"]!["inner_a"]!.AsLong());
        Assert.Equal(2L, node["outer"]!["inner_b"]!.AsLong());
        Assert.Equal("true", node["outer"]!["deep"]!["leaf"]!.AsString());
    }

    [Fact]
    public void Quoted_value_with_escaped_quote_and_backslash()
    {
        var node = AcfParser_Parse("""
            "Wrap"
            {
                "path"      "C:\\Program Files\\Steam"
                "title"     "He said \"hi\""
                "newline"   "line1\nline2"
            }
            """);

        Assert.Equal(@"C:\Program Files\Steam", node["path"]!.AsString());
        Assert.Equal("He said \"hi\"", node["title"]!.AsString());
        Assert.Equal("line1\nline2", node["newline"]!.AsString());
    }

    [Fact]
    public void Line_comments_are_ignored()
    {
        var node = AcfParser_Parse("""
            "Wrap"
            {
                // a comment
                "k1"   "v1"
                "k2"   "v2"  // trailing comment
            }
            """);

        Assert.Equal("v1", node["k1"]!.AsString());
        Assert.Equal("v2", node["k2"]!.AsString());
    }

    [Fact]
    public void Missing_keys_return_null_not_throw()
    {
        var node = AcfParser_Parse("""
            "Wrap" { "a" "1" }
            """);

        Assert.Null(node["nope"]);
        Assert.Null(node["WorkshopItemsInstalled"]?["1369573612"]);
    }

    [Fact]
    public void Real_appworkshop_fixture_parses_canonical_fields()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "appworkshop_552500_sample.acf");
        var node = AcfParser.ParseFile(path);

        Assert.Equal(552500L, node["appid"]!.AsLong());
        Assert.Equal(550697166L, node["SizeOnDisk"]!.AsLong());
        Assert.Equal(1778806775L, node["TimeLastUpdated"]!.AsLong());

        var items = node["WorkshopItemsInstalled"]!;
        Assert.True(items.IsObject);
        Assert.Equal(3, items.Children.Count);

        var ct = items["3712929235"]!;
        Assert.Equal(1399903L, ct["size"]!.AsLong());
        Assert.Equal(1778811142L, ct["timeupdated"]!.AsLong());
        Assert.Equal("8294719383951827463", ct["manifest"]!.AsString());
    }

    [Fact]
    public void Unterminated_object_tolerated()
    {
        // Truncated file (Steam was killed mid-write?) — we return what we got rather than throwing.
        var node = AcfParser_Parse("""
            "Wrap"
            {
                "k"  "v"
            """);
        Assert.Equal("v", node["k"]!.AsString());
    }

    [Fact]
    public void AsString_on_object_throws()
    {
        var node = AcfParser_Parse("""
            "W" { "obj" { "k" "v" } }
            """);

        Assert.Throws<InvalidOperationException>(() => node["obj"]!.AsString());
    }

    [Fact]
    public void Indexer_on_scalar_throws()
    {
        var node = AcfParser_Parse("""
            "W" { "k" "v" }
            """);

        Assert.Throws<InvalidOperationException>(() => _ = node["k"]!["nope"]);
    }

    // Helper to keep the test bodies tight.
    private static AcfNode AcfParser_Parse(string text) => AcfNode.Parse(text);
}

// Tiny shim so `AcfParser.ParseFile` reads nicely in test bodies; the real implementation
// lives on AcfNode (because that's the natural home for the factory).
internal static class AcfParser
{
    public static AcfNode ParseFile(string path) => AcfNode.ParseFile(path);
}
