using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class StalenessAuditorTests
{
    private static WorkshopItemLocal Local(long timeUpdated) =>
        new(552500, 100ul, timeUpdated, "manifest", 1234);

    private static WorkshopItemRemote Remote(
        long? time = null, int apiResult = 1, bool? banned = false, int? visibility = 0) =>
        new(100ul, "Title", time, 1234, visibility, banned, apiResult);

    [Fact]
    public void Local_equal_to_remote_is_Current()
    {
        var r = StalenessAuditor.Audit(Local(1000), Remote(time: 1000), "Game");
        Assert.Equal(FreshnessStatus.Current, r.Status);
    }

    [Fact]
    public void Local_after_remote_is_Current()
    {
        var r = StalenessAuditor.Audit(Local(2000), Remote(time: 1000), "Game");
        Assert.Equal(FreshnessStatus.Current, r.Status);
    }

    [Fact]
    public void Local_before_remote_is_Stale()
    {
        var r = StalenessAuditor.Audit(Local(1000), Remote(time: 2000), "Game");
        Assert.Equal(FreshnessStatus.Stale, r.Status);
    }

    [Fact]
    public void Null_remote_is_ApiFailed()
    {
        var r = StalenessAuditor.Audit(Local(1000), remote: null, "Game");
        Assert.Equal(FreshnessStatus.ApiFailed, r.Status);
    }

    [Fact]
    public void Banned_remote_is_Removed()
    {
        var r = StalenessAuditor.Audit(Local(1000), Remote(time: 2000, banned: true), "Game");
        Assert.Equal(FreshnessStatus.Removed, r.Status);
    }

    [Fact]
    public void ApiResult_9_is_Unknown_even_with_recent_timestamp()
    {
        // Friends-only / private item — API returns no timestamp; we can't classify.
        var r = StalenessAuditor.Audit(Local(1000), Remote(time: null, apiResult: 9), "Game");
        Assert.Equal(FreshnessStatus.Unknown, r.Status);
    }

    [Fact]
    public void Missing_remote_timestamp_is_Unknown()
    {
        var r = StalenessAuditor.Audit(Local(1000), Remote(time: null, apiResult: 1), "Game");
        Assert.Equal(FreshnessStatus.Unknown, r.Status);
    }

    [Fact]
    public void Banned_takes_priority_over_ApiResult9()
    {
        // Edge case: would never happen in practice (Steam doesn't return banned details
        // for non-public items), but defensive code path. Banned wins.
        var r = StalenessAuditor.Audit(Local(1000), Remote(time: null, apiResult: 9, banned: true), "Game");
        Assert.Equal(FreshnessStatus.Removed, r.Status);
    }

    [Fact]
    public void GameName_is_threaded_through()
    {
        var r = StalenessAuditor.Audit(Local(1000), Remote(time: 1000), "Vermintide 2");
        Assert.Equal("Vermintide 2", r.GameName);
    }
}
