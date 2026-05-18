using System.IO;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public class AcfWriterTests
{
    [Fact]
    public void Roundtrips_minimal_appworkshop_shape()
    {
        var original = """
            "AppWorkshop"
            {
                "appid"        "552500"
                "WorkshopItemsInstalled"
                {
                    "1369573612" { "size" "619982"  "timeupdated" "1688989995"  "manifest" "13341" }
                    "1374248490" { "size" "694440"  "timeupdated" "1636997427"  "manifest" "49365" }
                }
            }
            """;

        var node = AcfNode.Parse(original);
        var sw = new StringWriter();
        node.Write("AppWorkshop", sw);

        // Re-parse the output — round-trip equality at the semantic layer (whitespace differs).
        var reparsed = AcfNode.Parse(sw.ToString());
        Assert.Equal("552500",       reparsed["appid"]!.AsString());
        Assert.Equal("619982",       reparsed["WorkshopItemsInstalled"]!["1369573612"]!["size"]!.AsString());
        Assert.Equal("1636997427",   reparsed["WorkshopItemsInstalled"]!["1374248490"]!["timeupdated"]!.AsString());
    }

    [Fact]
    public void Remove_drops_the_installed_entry()
    {
        var src = """
            "AppWorkshop"
            {
                "WorkshopItemsInstalled"
                {
                    "1" { "size" "100" "timeupdated" "1" "manifest" "a" }
                    "2" { "size" "200" "timeupdated" "2" "manifest" "b" }
                }
            }
            """;
        var node = AcfNode.Parse(src);
        Assert.True(node["WorkshopItemsInstalled"]!.Remove("1"));
        Assert.False(node["WorkshopItemsInstalled"]!.Remove("99"));   // absent → false

        var sw = new StringWriter();
        node.Write("AppWorkshop", sw);
        var reparsed = AcfNode.Parse(sw.ToString());

        Assert.Null(reparsed["WorkshopItemsInstalled"]!["1"]);
        Assert.Equal("200", reparsed["WorkshopItemsInstalled"]!["2"]!["size"]!.AsString());
    }

    [Fact]
    public void Escapes_quotes_and_backslashes_in_values()
    {
        var src = """"
            "AppWorkshop" { "k" "v with \"quote\" and \\back" }
            """";
        var node = AcfNode.Parse(src);
        Assert.Equal("v with \"quote\" and \\back", node["k"]!.AsString());

        var sw = new StringWriter();
        node.Write("AppWorkshop", sw);
        var reparsed = AcfNode.Parse(sw.ToString());
        Assert.Equal("v with \"quote\" and \\back", reparsed["k"]!.AsString());
    }
}
