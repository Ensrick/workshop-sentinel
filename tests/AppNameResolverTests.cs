using System;
using System.IO;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class AppNameResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public AppNameResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "WSAppName_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_tempRoot, "steamapps"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Reads_name_from_appmanifest()
    {
        var manifest = Path.Combine(_tempRoot, "steamapps", "appmanifest_552500.acf");
        File.WriteAllText(manifest, """
            "AppState"
            {
                "appid"     "552500"
                "name"      "Warhammer: Vermintide 2"
                "installdir"    "Warhammer Vermintide 2"
            }
            """);
        var resolver = new AppNameResolver(new[] { _tempRoot });

        Assert.Equal("Warhammer: Vermintide 2", resolver.Resolve(552500u));
    }

    [Fact]
    public void Falls_back_when_manifest_missing()
    {
        var resolver = new AppNameResolver(new[] { _tempRoot });

        Assert.Equal("App 999999", resolver.Resolve(999999u));
    }

    [Fact]
    public void Caches_repeated_lookups()
    {
        var manifest = Path.Combine(_tempRoot, "steamapps", "appmanifest_552500.acf");
        File.WriteAllText(manifest, """ "AppState" { "name" "First" } """);
        var resolver = new AppNameResolver(new[] { _tempRoot });

        var first = resolver.Resolve(552500u);

        // Mutate the file underneath — cache should keep returning the original.
        File.WriteAllText(manifest, """ "AppState" { "name" "Mutated" } """);
        var second = resolver.Resolve(552500u);

        Assert.Equal("First", first);
        Assert.Equal("First", second);
    }

    [Fact]
    public void Searches_multiple_library_roots()
    {
        var second = Path.Combine(Path.GetTempPath(), "WSAppName2_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(second, "steamapps"));
        try
        {
            File.WriteAllText(
                Path.Combine(second, "steamapps", "appmanifest_730.acf"),
                """ "AppState" { "name" "Counter-Strike 2" } """);
            var resolver = new AppNameResolver(new[] { _tempRoot, second });

            Assert.Equal("Counter-Strike 2", resolver.Resolve(730u));
        }
        finally
        {
            try { Directory.Delete(second, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Corrupt_manifest_falls_back_silently()
    {
        File.WriteAllText(Path.Combine(_tempRoot, "steamapps", "appmanifest_123.acf"), "not acf");
        var resolver = new AppNameResolver(new[] { _tempRoot });

        Assert.Equal("App 123", resolver.Resolve(123u));
    }
}
