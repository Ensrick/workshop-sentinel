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
///
/// <para><c>FileType</c> values that matter for filtering non-mod items out of a friend's
/// sub list: 0=Community (the normal "user-published mod"), 13=ControllerBinding (Steam
/// Controller config — shows up under any game's appid filter because Steam tags controller
/// configs to the game they're for), 2=Collection, 3=Art, 5=Screenshot. Only file_type 0
/// (and occasionally 7=Game) survives the "is this an actual mod?" filter.</para>
///
/// <para><c>ConsumerAppId</c> is the appid the item is *intended for*. Steam's
/// <c>myworkshopfiles?appid=N</c> URL filter is loose — items targeting other apps still
/// leak through. Match this against the selected game's appid to scrub them.</para>
/// </summary>
public sealed record WorkshopItemRemote(
    ulong   PublishedFileId,
    string? Title,
    long?   RemoteTimeUpdated,   // unix epoch seconds — when the author last updated the item
    long?   RemoteSizeBytes,
    int?    Visibility,          // 0=public, 1=friends, 2=private
    bool?   Banned,
    int     ApiResult,           // 1=ok, 9=no permission, etc.
    int?    FileType = null,     // 0=Community/mod, 13=ControllerBinding, etc. — null when API result != 1
    uint?   ConsumerAppId = null);

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
