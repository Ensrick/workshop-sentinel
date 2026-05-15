namespace WorkshopSentinel.Services;

/// <summary>
/// Local Workshop item state, parsed from a single entry under
/// `WorkshopItemsInstalled.&lt;publishedfileid&gt;` in `appworkshop_&lt;appid&gt;.acf`.
/// </summary>
public sealed record WorkshopItemLocal(
    uint   AppId,
    ulong  PublishedFileId,
    long   LocalTimeUpdated,   // unix epoch seconds — when Steam last downloaded this item
    string LocalManifest,
    long   LocalSizeBytes);

/// <summary>
/// Remote Workshop item state, fetched from the Steam Web API. Nullable fields stay nullable
/// when the API returns result=9 (no permission, friends-only without key).
/// </summary>
public sealed record WorkshopItemRemote(
    ulong   PublishedFileId,
    string? Title,
    long?   RemoteTimeUpdated,   // unix epoch seconds — when the author last updated the item
    long?   RemoteSizeBytes,
    int?    Visibility,          // 0=public, 1=friends, 2=private
    bool?   Banned,
    int     ApiResult);          // 1=ok, 9=no permission, etc.

public enum FreshnessStatus
{
    Current,    // local timestamp >= remote timestamp
    Stale,      // local timestamp < remote timestamp
    Unknown,    // remote data unavailable (friends-only without API key)
    Removed,    // remote reports banned / item missing
    ApiFailed,  // transient API failure
}

/// <summary>One row in the audit result grid.</summary>
public sealed record AuditedItem(
    WorkshopItemLocal   Local,
    WorkshopItemRemote? Remote,
    string?             GameName,
    FreshnessStatus     Status);
