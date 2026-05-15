namespace WorkshopSentinel.Services;

/// <summary>
/// Pure function: classify a (local, remote) pair as Current / Stale / Unknown / Removed / ApiFailed.
/// Status priority (first matching rule wins):
///   1. remote == null            → ApiFailed (no network response at all)
///   2. remote.Banned == true     → Removed   (item was banned / pulled from Workshop)
///   3. remote.ApiResult != 1     → Unknown   (typically 9 = no permission for non-public)
///   4. remote.RemoteTimeUpdated is null → Unknown (response missing the timestamp)
///   5. local &gt;= remote        → Current
///   6. local &lt; remote         → Stale
/// </summary>
public static class StalenessAuditor
{
    public static AuditedItem Audit(WorkshopItemLocal local, WorkshopItemRemote? remote, string? gameName)
    {
        var status = ClassifyStatus(local, remote);
        return new AuditedItem(local, remote, gameName, status);
    }

    private static FreshnessStatus ClassifyStatus(WorkshopItemLocal local, WorkshopItemRemote? remote)
    {
        if (remote is null)                        return FreshnessStatus.ApiFailed;
        if (remote.Banned == true)                 return FreshnessStatus.Removed;
        if (remote.ApiResult != 1)                 return FreshnessStatus.Unknown;
        if (remote.RemoteTimeUpdated is null)      return FreshnessStatus.Unknown;
        return local.LocalTimeUpdated >= remote.RemoteTimeUpdated.Value
            ? FreshnessStatus.Current
            : FreshnessStatus.Stale;
    }
}
