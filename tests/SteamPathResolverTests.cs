using System;
using System.IO;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class SteamPathResolverTests : IDisposable
{
    private readonly string _tempDir;

    public SteamPathResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WSTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Override_takes_precedence_over_registry()
    {
        var settings = new Settings { SteamPathOverride = _tempDir };
        var resolver = new SteamPathResolver(settings, probeRegistry: () => @"D:\SomeOtherSteam");

        Assert.Equal(_tempDir, resolver.Resolve());
    }

    [Fact]
    public void Falls_back_to_registry_when_no_override()
    {
        var settings = new Settings();
        var resolver = new SteamPathResolver(settings, probeRegistry: () => _tempDir);

        Assert.Equal(_tempDir, resolver.Resolve());
    }

    [Fact]
    public void Override_pointing_at_nonexistent_dir_throws()
    {
        var settings = new Settings { SteamPathOverride = @"Z:\definitely-not-a-real-dir" };
        var resolver = new SteamPathResolver(settings, probeRegistry: () => null);

        Assert.Throws<SteamNotFoundException>(() => resolver.Resolve());
    }

    [Fact]
    public void All_sources_empty_throws()
    {
        var settings = new Settings();
        var resolver = new SteamPathResolver(settings, probeRegistry: () => null);

        Assert.Throws<SteamNotFoundException>(() => resolver.Resolve());
    }

    [Fact]
    public void TryResolve_swallows_exception_returns_false()
    {
        var settings = new Settings();
        var resolver = new SteamPathResolver(settings, probeRegistry: () => null);

        Assert.False(resolver.TryResolve(out var path));
        Assert.Equal("", path);
    }
}
