# Workshop Sentinel — Plan

> Working name. Could also be: Workshop Watcher / Workshop Sync / Mod Fresh / etc.

A small Windows app (and matching headless CLI) that audits every Steam Workshop subscription on the machine — across every installed game — and tells you which items are stale relative to what's actually published. One-click "refresh" forces the listed items to re-download.

This document is the design plan. **It is not a build instruction yet.** Once you sign off (or send corrections), the implementation plan splits into versioned milestones.

---

## 1. Problem

Steam silently caches Workshop content. Two failure modes:

- **Subscriber stale**: a mod is updated by its author, but Steam doesn't re-download to your machine — usually because Steam thinks the local copy is current, or because Steam was offline when the update landed, or because the local manifest got out of sync.
- **Author stale**: you just uploaded a new revision and want to confirm everyone is actually pulling the new bytes, not whatever Steam cached in their content folder.

There is **no built-in Steam UI** for either case. The only manual workaround is "unsubscribe, delete folder, resubscribe" per item — which is fine for one mod and absurd for the 200-item Workshop subscription list a typical Skyrim / Cities player carries.

## 2. Research findings

What we know after probing the relevant Steam surfaces.

### 2.1 Local Workshop state lives in `appworkshop_<appid>.acf`

One file per game the user has subscriptions in, all sitting in
`<Steam>\steamapps\workshop\appworkshop_<appid>.acf`.

Format: Valve KeyValues (the same plain-text format as `appmanifest_*.acf`). Multiple Lua / Python / JS / .NET parsers exist; the trivial recursive `{ key "value" }` grammar is easy to hand-roll. Real sample from this machine:

```
"AppWorkshop"
{
  "appid"             "552500"
  "SizeOnDisk"        "550697166"
  "NeedsUpdate"       "0"
  "NeedsDownload"     "0"
  "TimeLastUpdated"   "1778806775"
  "TimeLastAppRan"    "1778806428"
  "LastBuildID"       "22174928"
  "WorkshopItemsInstalled"
  {
    "1369573612" { "size" "619982"  "timeupdated" "1688989995"  "manifest" "13341..." }
    "1374248490" { "size" "694440"  "timeupdated" "1636997427"  "manifest" "49365..." }
    ...
  }
  "WorkshopItemDetails"
  {
    "<published_file_id>" { "manifest" "..." "ugchandle" "..." "timeupdated" "..." "timetouched" "..." }
  }
}
```

Key fields per subscribed item:

| Field          | Meaning                                                        |
|----------------|----------------------------------------------------------------|
| `size`         | Bytes on disk for this item                                    |
| `timeupdated`  | Unix epoch — when **the local copy was last refreshed**        |
| `manifest`     | Steam depot manifest ID — bumped per published revision        |

> ⚠️ Caveat: per-item `timeupdated` is the *download time*, not the *publish time*. It's still the right comparison key — if `local.timeupdated < remote.time_updated`, the local copy is older than what's live.

This machine has at least 6 `appworkshop_*.acf` files visible right now (VT2 = 552500, plus 5 other games). Real-world users have dozens.

### 2.2 Steam Web API — public items work without auth

**`POST https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/`**
Body: `itemcount=N&publishedfileids[0]=...&publishedfileids[1]=...`

No API key required. Tested live against ct (3712929235): full response with every interesting field —

```
publishedfileid, creator, creator_app_id, consumer_app_id, file_size,
preview_url, title, description, time_created, time_updated, visibility,
banned, ban_reason, subscriptions, favorited, lifetime_subscriptions,
lifetime_favorited, views, tags
```

`visibility`: `0` = public, `1` = friends-only, `2` = private. (We see "visibility=0" for public ct.)

**The friends-only / private gap.** Tested live against wt (3712896117, friends-only): the same endpoint returns just `result: 9` and no fields. That means: this endpoint cannot tell us anything about non-public items without authentication. Confirmed; not theoretical.

**Authenticated path: `IPublishedFileService/GetDetails/v1/`** with a free Steam Web API key (anyone can request one at https://steamcommunity.com/dev/apikey). 401 Unauthorized without a key. Presumed to return friends-only / private items the user has access to — needs hands-on verification with the user's own key during implementation.

Batching: GetPublishedFileDetails is documented as a batch endpoint with no hard cap I've found. Reasonable batch size: 100 IDs/request. With ~5000 subscriptions across all games on a typical user's machine, that's ~50 round-trips — easy.

### 2.3 Refresh mechanisms

Three real options, in order of preference:

**A. `steam://workshop_download_item/<appid>/<publishedfileid>`** — Steam's URL protocol handler. Confirmed real (it's the URL form of the SteamCMD `workshop_download_item` console command, used internally by Steam's own UI). Requires Steam to be running. For an item the user is already subscribed to, this *should* nudge Steam to re-acquire it. **But** Steam's client makes the final call on whether to actually re-download or just say "already current"; if Steam thinks the local manifest matches the live one, the URL is a no-op.

**B. Hard reset: delete local folder + send the steam:// URL.** Brute force but reliable. Steps:
1. Stop Steam (or at least Workshop downloads).
2. Delete `<Steam>\steamapps\workshop\content\<appid>\<itemid>\`.
3. Remove the per-item entry from `appworkshop_<appid>.acf` (`WorkshopItemsInstalled.<itemid>` block).
4. Restart Steam → it sees a subscribed item with no local copy → re-downloads from scratch.

This bypasses Steam's "I think it's current" optimization. Risk: if Steam is mid-edit on the ACF when we touch it, we corrupt it. Mitigation: stop Steam first; back up the ACF before editing; merge in-place rather than rewrite.

**C. SteamCMD `workshop_download_item`** — works headlessly without Steam Desktop running, but requires either anonymous login (only works on a small "Anonymous Dedicated Server Comp" whitelist; VT2 isn't on it) or the user's Steam credentials. Out of scope for v1 unless we want to support a "download mods you don't subscribe to" use case.

### 2.4 No existing tool covers this

I checked. There's a long-standing Steam feature request for a "Verify Workshop" button equivalent to "Verify integrity of game files" — never built. Per-game tools exist for specific titles (CS-compat reports, etc.) but nothing general-purpose. Gap is real.

---

## 3. MVP scope (v0.1.0)

What ships in the first version. Deliberately tight.

**In:**
- Auto-detect Steam install path (registry: `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`). Manual override available.
- Enumerate every `appworkshop_*.acf` file in `<Steam>\steamapps\workshop\`.
- Parse each into a list of `(appid, published_file_id, local_timeupdated, local_manifest, local_size)`.
- For each app, look up the human-readable game name via `appmanifest_<appid>.acf` (already on disk for any installed game).
- Batch-query `ISteamRemoteStorage/GetPublishedFileDetails` (100 IDs/request) to fetch `time_updated`, `file_size`, `title`, `visibility` for every subscribed item.
- Show a sortable table: `Game | Item title | Local time | Live time | Δ | Size | Status`.
- `Status` column resolves to one of: **Current** (local ≥ live), **Stale** (live > local), **Untracked** (live data missing — could be private / removed / API failure), **Failed** (couldn't query).
- "Refresh selected" button: for each selected item, runs **option B** (hard reset) — verify Steam isn't actively downloading the item, delete content folder, strip the manifest entry, prompt the user to restart Steam.
- "Open in Steam" button per item: `steam://url/CommunityFilePage/<id>`.
- Settings: Steam path override, API key (optional — only enables private-item auditing in v2).
- Read-only, opt-in destructive actions, every "Refresh" action goes through a confirmation modal naming the item count + total bytes about to be deleted.

**Out (deferred):**
- Private / friends-only item auditing (needs the user's API key + `IPublishedFileService`).
- SteamCMD path for downloading items the user isn't subscribed to.
- Cross-machine sync, scheduled audits, system tray daemon.
- Bandwidth / time estimates for batch refresh.
- Auto-restart Steam.

## 4. Versioned milestones

| Version | Scope                                                                                                  | Why                                 |
|---------|--------------------------------------------------------------------------------------------------------|-------------------------------------|
| 0.1.0   | MVP per §3. Public items only.                                                                         | Proves the value end-to-end.        |
| 0.2.0   | Optional API-key field. `IPublishedFileService` path for friends-only / private items the user owns.   | Closes the obvious gap.             |
| 0.3.0   | Headless CLI: `audit [--game <appid>] [--json]`, `refresh <item-id>`, `list`, `doctor`. Mirrors VMBLauncher's GUI/CLI hybrid pattern. | Scriptability + CI use cases.       |
| 0.4.0   | "Watch mode": polls API every N minutes, system tray icon turns yellow when anything new is stale.     | Background-helper use case.         |
| 0.5.0   | Bulk operations: "select all stale across all games"; export audit report to CSV / JSON.               | Power-user QoL.                     |
| Later   | SteamCMD path for un-subscribed downloads. Author-mode publish manifests. Compatibility hints.         | Speculative.                        |

## 5. Tech stack

Same shape as the user's existing VMBLauncher (so a familiar code style and we can reuse helpers if useful):

- **.NET 9, C#**, single-file self-contained Windows binary.
- **WPF** for the GUI window. `OutputType=Exe` (Console subsystem) + `FreeConsole()` for the GUI path so the same binary handles both modes.
- **HttpClient** (built-in) for the Steam Web API — no third-party dep.
- **Hand-rolled ACF parser**, ~100 LOC. The format is trivial; pulling in a library is over-kill and matches VMBLauncher's "few deps, all visible" philosophy.
- **xUnit** for tests, parity with VMBLauncher.
- **`publish.ps1`** mirroring VMBLauncher's: runs tests, builds, opens output folder.

Folder layout (proposed):

```
workshop-sentinel/
├── Program.cs                       # GUI vs headless branching
├── App.xaml / App.xaml.cs           # WPF app entry
├── MainWindow.xaml / .cs            # Main grid view
├── Views/
│   ├── SettingsDialog.xaml
│   └── ConfirmRefreshDialog.xaml
├── Cli/
│   ├── Verbs/
│   │   ├── AuditCommand.cs
│   │   ├── RefreshCommand.cs
│   │   ├── ListCommand.cs
│   │   └── DoctorCommand.cs
│   └── CliRunner.cs
├── Services/
│   ├── AcfParser.cs                 # KeyValues -> object tree
│   ├── SteamPathResolver.cs         # Registry + override
│   ├── WorkshopEnumerator.cs        # Reads all appworkshop_*.acf
│   ├── SteamWebApiClient.cs         # ISteamRemoteStorage batched
│   ├── AppNameResolver.cs           # appmanifest_*.acf -> game name
│   ├── StalenessAuditor.cs          # Compares local vs remote
│   ├── RefreshExecutor.cs           # Hard-reset flow
│   └── SettingsStore.cs             # %APPDATA%\WorkshopSentinel\settings.json
├── tests/
│   ├── AcfParserTests.cs
│   ├── SteamWebApiClientTests.cs    # Mocked HttpMessageHandler
│   ├── StalenessAuditorTests.cs
│   └── EndToEndSmoke.ps1
├── publish.ps1
├── PLAN.md                          # this file
└── README.md
```

## 6. Data model

```csharp
public sealed record WorkshopItemLocal(
    uint   AppId,
    ulong  PublishedFileId,
    long   LocalTimeUpdated,    // unix epoch
    string LocalManifest,
    long   LocalSizeBytes);

public sealed record WorkshopItemRemote(
    ulong  PublishedFileId,
    string Title,
    long   RemoteTimeUpdated,   // unix epoch
    long   RemoteSizeBytes,
    int    Visibility,          // 0=public, 1=friends, 2=private
    bool   Banned,
    int    ApiResult);          // 1=ok, 9=no permission, etc.

public enum FreshnessStatus { Current, Stale, Unknown, Removed, ApiFailed }

public sealed record AuditedItem(
    WorkshopItemLocal  Local,
    WorkshopItemRemote? Remote,
    string?            GameName,
    FreshnessStatus    Status);
```

The audit is a pure function from (`List<WorkshopItemLocal>`, `Dictionary<ulong, WorkshopItemRemote>`) → `List<AuditedItem>`. Easy to unit-test.

## 7. UI sketch (text)

```
┌─ Workshop Sentinel ─────────────────────────────────────────────────[_][□][×]┐
│ File   Help                                                                   │
│ ┌─Toolbar────────────────────────────────────────────────────────────────────┐ │
│ │ [⟳ Refresh audit]  [✓ Refresh selected (3)]  [⚙ Settings]  [Filter: stale ▾]│ │
│ └────────────────────────────────────────────────────────────────────────────┘ │
│ ┌────────────────────────────────────────────────────────────────────────────┐ │
│ │ □ Game                Item title                Local        Live    Δ  Size│ │
│ │ ☑ Vermintide 2        Tweaker: Chaos Wastes    2d ago       5min  ⚠ 1.4MB │ │
│ │ ☐ Vermintide 2        VMF                      30d ago      30d    ✓ 620KB │ │
│ │ ☐ Skyrim SE           Unofficial Patch         12h ago      —    untracked │ │
│ │ ☑ Cities: Skylines    81 Tiles                 220d ago    10d  ⚠⚠ 2.1MB  │ │
│ │ ...                                                                         │ │
│ │ ☑ Tabletop Simulator  ─                        4d ago       —    removed   │ │
│ └────────────────────────────────────────────────────────────────────────────┘ │
│ ┌─Status bar─────────────────────────────────────────────────────────────────┐ │
│ │ 1,247 subscribed · 42 stale · 12 untracked · 3 removed · last audit 17:42  │ │
│ └────────────────────────────────────────────────────────────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────────┘
```

- Sort by any column; default sort = staleness Δ descending.
- Filter dropdown: `all | stale only | untracked only | removed only`.
- Per-row right-click: Open in Steam, Open content folder, Copy ID, Force re-download this item.
- Status bar = quick stats + last-audit timestamp.

## 8. Headless CLI (v0.3.0)

Mirror VMBLauncher's verb/exit-code convention so muscle-memory carries over:

```
workshop-sentinel list                          # list all subscribed items
workshop-sentinel list --stale                  # only stale
workshop-sentinel list --game 552500            # only that appid
workshop-sentinel list --json                   # machine-readable

workshop-sentinel audit                         # full audit, print summary
workshop-sentinel audit --json                  # full audit, JSON to stdout

workshop-sentinel refresh <item-id> [<item-id>] [--yes]
                                                # force-refresh items, --yes skips confirm

workshop-sentinel doctor                        # diagnostics (Steam path, API reachable, etc.)
workshop-sentinel help
```

Exit codes:
- 0 OK
- 1 Runtime error
- 2 Bad usage
- 3 Preflight failed (Steam path missing, etc.)

Same `--gui` / `--no-banner` / `--config` flags as VMBLauncher.

## 9. Risk register

| Risk                                                                  | Mitigation                                                                              |
|-----------------------------------------------------------------------|-----------------------------------------------------------------------------------------|
| ACF parser doesn't handle every edge case (escaped chars, comments)   | Test against every `appworkshop_*.acf` on this machine + fuzz parser tests.             |
| Steam holds an exclusive lock on the ACF when running                 | Read-only by default; only modify after the user confirms refresh and we've nudged them to close Steam. |
| Steam writes the ACF between our read and our edit                    | Re-read + diff before write; abort and ask user to retry if the file changed.           |
| `steam://workshop_download_item` no-ops because Steam thinks current  | Only used as a hint after we've already deleted the local folder.                       |
| API rate-limit / temporary 429                                        | Exponential backoff + caching responses with TTL (15 min) by item ID.                   |
| Private items in user's own subscriptions look "untracked" forever    | v0.2.0 adds API key flow; v0.1.0 documents this clearly in the UI tooltip.              |
| User uses Steam on a non-default drive                                | Registry lookup + manual path override + recursive `appworkshop_*.acf` search if needed. |
| User accidentally deletes folders for an item they didn't mean to     | Confirmation modal lists exact paths + sizes; "Refresh selected" requires checkbox.     |
| `appworkshop_<appid>.acf` schema changes in a Steam client update     | All field reads via tolerant getters; missing fields warn, don't crash.                 |
| Cross-drive Steam libraries (libraryfolders.vdf points elsewhere)     | Parse `<Steam>\steamapps\libraryfolders.vdf` to enumerate every library root.           |

## 10. Open questions for you

Things I'd want answered before locking the spec:

1. **Author-mode**: do you also want a "I just uploaded, force every subscriber to refresh" mode? That can't be done from outside Steam — but a "force MY copy to be the canonical one for a follow-up local test" use case could be a polish item.
2. **Auto-restart Steam after refresh**? Convenient but invasive — Steam might be downloading something else, or running a friend's chat. I lean toward "no — show a banner saying 'restart Steam to apply'."
3. **Where should the binary live**? Standalone repo (`github.com/Ensrick/workshop-sentinel`) or inside vermintide-2-tweaker/tools/ as a sibling of vmb-launcher?
4. **Public release**? If yes, what visibility (open-source on GitHub, signed exe distribution, Steam app one day)? If just personal, we can skip the SmartScreen-warning workflow.
5. **API key handling**: if v0.2.0 adds the API-key path, do you want it stored plaintext in `%APPDATA%`, or DPAPI-encrypted (per-user)? VMBLauncher stores plaintext today.
6. **Naming**: keep "Workshop Sentinel" or pick another?

---

## What I want from you on this draft

- Spot any wrong assumption or missing constraint.
- Confirm or revise the MVP scope (§3) — that's the load-bearing decision.
- Answer the open questions in §10 you care about.

After your sign-off the next document is `IMPLEMENTATION.md` — milestone-by-milestone task breakdown with concrete commits.
