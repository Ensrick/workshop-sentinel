using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public class SteamProcessGuardTests
{
    [Fact]
    public void IsSteamRunning_true_when_steam_in_list()
    {
        var guard = new SteamProcessGuard(() => new[] { "explorer", "Steam", "chrome" });
        Assert.True(guard.IsSteamRunning());
    }

    [Fact]
    public void IsSteamRunning_false_when_absent()
    {
        var guard = new SteamProcessGuard(() => new[] { "explorer", "chrome" });
        Assert.False(guard.IsSteamRunning());
    }

    [Fact]
    public void IsSteamRunning_case_insensitive()
    {
        var guard = new SteamProcessGuard(() => new[] { "STEAM" });
        Assert.True(guard.IsSteamRunning());
    }
}
