# Changelog

All notable changes to Workshop Sentinel are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.0] — 2026-05-19

### Added — per-row Subscribe button on the friend-compare tab

The Compare to Friends tab gets a new **Action** column at the right edge of the matrix. For every mod a friend has subscribed to that the local user doesn't, the cell renders a clickable **Subscribe** button. Click → Workshop Sentinel reads Steam's authenticated CEF session cookies and POSTs to `steamcommunity.com/sharedfiles/subscribe` to subscribe the local Steam account on the spot. Steam's server-side subscription is immediate; the client-side `appworkshop_<appid>.acf` updates on the next sub-monitor tick (~10 min) or on next Steam launch.

This replaces the previous "look at the matrix, manually navigate to each Workshop page in a browser, click Subscribe per item" workflow that made the friend-compare tab feel like a dead-end.

#### Mechanism (verified live 2026-05-17 against PC-B)

1. **`Services/SteamCefCookieReader.cs`** — extracts `steamLoginSecure` + `sessionid` from Steam's CEF webhelper cookie store at `%LOCALAPPDATA%\Steam\htmlcache\`. Pipeline: read `Local State` JSON → DPAPI-unwrap the `os_crypt.encrypted_key` (strip the 5-byte "DPAPI" prefix, `ProtectedData.Unprotect` under `CurrentUser` scope) → 32-byte AES-256 master key. Copy the Chromium-format `Cookies` SQLite DB to temp to dodge the live-DB lock, query `WHERE host_key LIKE '%steamcommunity.com%'`, AES-GCM-decrypt each `encrypted_value` (v10/v11 format: 3-byte prefix + 12-byte IV + ciphertext + 16-byte GCM tag). Returns a structured `(Outcome, Cookies, ErrorDetail)` result so the UI can render friendly errors per failure mode (Steam not installed, Windows-user mismatch, cookies missing because the user logged out, etc.).
2. **`Services/SteamSubscribeClient.cs`** — POSTs `id=<workshopid>&appid=<appid>&sessionid=<sessionid>` to the subscribe endpoint with `Origin`, `Referer`, `X-Requested-With: XMLHttpRequest`, and the two cookies set on a per-request `Cookie` header. Parses `{"success":1}` as Ok; any other shape as `ServerRejected` (banned item, removed, no visibility) or `MalformedResponse`. 401/403 → `AuthFailed` with a hint about re-logging into Steam.
3. **`MainWindow.MakeSubscribeColumn` + `OnSubscribeRowClicked`** — the cookie reader runs lazily on first click (one DPAPI/SQLite read per session), then every subsequent subscribe just makes an HTTP call. On success the row's `[You]` cell flips from `·` to `↻` ("pending Steam-client sync") and the button hides; on failure a friendly dialog explains the outcome.

#### Privacy detection rewrite

Old `FriendSubscriptionsClient` detected private profiles by regex-matching HTML phrases like "This profile is private". Modern Steam (verified May 2026) no longer emits any of those phrases — a public-with-zero-subs page and a friends-only-with-hidden-subs page render with identical HTML. Result: the old regex never fired and private profiles silently surfaced as "0 subscriptions found." The new path **pre-flights** the XML profile endpoint (`profiles/<sid>/?xml=1`) and gates on `<visibilityState>` (3=Public, 2=FriendsOnly, 1=Private). That's a structured signal Steam guarantees and we already use for online-state. Network blips on the XML probe fall through to the scrape (so a transient timeout doesn't render as a permanent privacy false-positive).

#### Files

- New: `Services/SteamCefCookieReader.cs`, `Services/SteamSubscribeClient.cs`, `tests/SteamCefCookieReaderTests.cs`, `tests/SteamSubscribeClientTests.cs` (18 new unit tests covering the cookie-blob format end-to-end, DPAPI key-loading edge cases, HTTP response parsing, header/cookie wire format, and failure-mode mapping).
- Changed: `Services/FriendSubscriptionsClient.cs` — drops `PrivateProfileRegex`, adds `ProfileVisibility` enum + `ParseVisibility` + `FetchVisibilityAsync` + `FetchSubscriptionsAsync` pre-flight. `tests/FriendSubscriptionsClientTests.cs` updated to stub both the XML probe and the scrape pages.
- Changed: `MainWindow.xaml.cs` — `_subscribeClient` lazy field, `MakeSubscribeColumn` factory, `OnSubscribeRowClicked` handler, `EnsureSubscribeClientAsync` cookie initializer, `EmptyStringToVisibilityConverter` (hides the button on owned rows). Row data gains an `[Action]` column with "Subscribe" / "" semantics.
- Dependency: `Microsoft.Data.Sqlite 9.0.0` — needed to read Chromium's encrypted cookie DB. Adds ~5 MB to the self-contained binary.

### Changed — non-destructive Workshop refresh (the amanda fix)

The "hard reset" Workshop refresh that lost amanda's 20+ VT2 mods on 2026-05-19 has been replaced with a **mutation-only** flow. `Services/RefreshExecutor.RefreshAsync` no longer deletes the local content directory. Instead it:

1. Snapshots `appworkshop_<appid>.acf` to `appworkshop_<appid>.acf.workshop-sentinel.bak` (roll-back path).
2. Mutates the ACF in place: top-level `NeedsDownload="1"` plus per-item `timeupdated="1"` and `manifest="-1"` in BOTH `WorkshopItemsInstalled.<id>` and `WorkshopItemDetails.<id>`. The per-item blocks themselves stay present; `ugchandle` and `size` are left alone. Atomic via `.workshop-sentinel.tmp` + `File.Replace`, with the same mtime-guard against mid-flight edits by Steam.
3. Belt-and-suspenders `steam://workshop_download_item/<appid>/<itemid>` per item. If Steam ignores the URL (foreground state, sub cache, etc.), the ACF mutation still triggers Steam's sub-monitor on its next tick (~10 min) or immediately on next Steam launch.
4. Verifies by polling each item's `timeupdated` field in the live ACF for up to 60 s. `VerifyOk` fires the moment Steam flips the sentinel back to a real Unix epoch (≥ 2020-01-01) — that's the unambiguous "Steam re-pulled" signal. `VerifyFailed` items have their outcome flipped to failed, but **their local content folder is untouched the whole time**, so failure is recoverable: try again, restart Steam, or wait for the next sub-monitor tick.

### Why this fixes the amanda burn

The old flow's failure mode was unrecoverable: it deleted local content, stripped the manifest, fired `steam://workshop_download_item/`, and IF Steam silently dropped the URL (which it did, 20+ times for amanda), the user was left with NO local copy AND no re-download. The new flow inverts the risk: the ACF is mutated, content stays put, and a `.bak` snapshot makes rollback trivial. Worst case is "Steam doesn't pull and I have to try again with my old copy still intact."

### Implementation notes

- `RefreshExecutor.MutateAcf` replaces `RefreshExecutor.RewriteAcf`. Same atomic-rename + mtime-guard semantics; mutates in-place instead of dropping subtree keys.
- `AcfNode` gains a `SetScalar(key, value)` mutator alongside the existing `Remove(key)`.
- `RefreshAsync` has two new optional parameters: `TimeSpan? verifyTimeout` (default 60 s; pass `TimeSpan.Zero` to opt out) and `Func<string, ulong, long>? readItemTimeUpdated` (test injection — reads `WorkshopItemsInstalled.<id>.timeupdated` from disk; tests stub it).
- Step stages changed: BeginItem / `Snapshot` / `MutateAcf` / EmitDownloadUrl / CompleteItem / VerifyStart / VerifyOk / VerifyFailed / Skip / Error. Removed: `DeleteContent`, `RewriteAcf`.
- `RefreshItemsAsync` in `MainWindow.xaml.cs`: dropped the "Steam is running, the delete will fail" warning (no more delete to fail) and rewrote the confirmation + completion dialogs around "marking as stale" / "Steam re-pulled" / "your content is untouched." Removed unused `SteamProcessGuard` field.
- Tests rewritten: `MutateAcf_flips_sentinels_in_both_subtrees_and_sets_top_level_NeedsDownload`, `MutateAcf_noop_when_no_ids_match`, plus three end-to-end `RefreshAsync` tests covering the happy path, ACF-missing error, verify timeout, and verify success. The verify-failed test asserts the content directory still exists (critical for the data-loss-fix invariant).

### Field-level mechanism

The per-item `manifest="-1"` sentinel matches Valve's own "unset" marker (visible in the canonical `appworkshop_322330.acf` sample). The top-level `NeedsDownload="1"` is the proven trigger from the rFactor 2 community thread where users were complaining Steam re-downloaded ALL their workshop content unprompted whenever the local manifest looked invalid — exactly the behavior we want to weaponize on demand.

## [0.2.2] — 2026-05-18

### Added — post-refresh verification

The "hard reset" Workshop refresh (`RefreshExecutor.RefreshAsync`) now has a fourth phase: after emitting the `steam://workshop_download_item/<appid>/<itemid>` nudges, poll each item's content directory for up to 30 s and emit `VerifyOk` as soon as Steam materializes new files there. Items whose dir stays empty when the window elapses get a `VerifyFailed` step and have their `RefreshOutcome.Success` flipped to false. The GUI's wrap-up dialog now distinguishes "verification failed" from generic refresh failures and tells the user exactly what to do: open Steam, navigate to each affected item's Workshop page, click Subscribed → Subscribe to bump the server-side subscription state.

### Burning case

On 2026-05-19 a user (amanda) ran the refresh batch on every mod in her Vermintide 2 install (~20 items). Steam silently ignored every `steam://` nudge — most likely because Steam wasn't the foreground process when the URLs fired, or because Steam's subscription cache thought "I already have these items so the URL is a no-op." All 20 content dirs stayed empty. ModManager then logged `Mod with id <X> is missing .mod file, skipping` for every workshop_id at next game launch, no `mod_script` ran, and her in-game `NetworkLookup.deus_power_up_templates` was therefore vanilla-only (~155 entries). When she rejoined a friend's lobby that had ct (Tweaker: Chaos Wastes) injecting boons up to network index 177, the friend's `rpc_shared_state_set_server_string` broadcast crashed her client at `network_lookup.lua:2514`. The refresh tool had told her "Refresh complete — Succeeded: 20" because phase 3 reported success on every steam:// emission. There was no signal at all that the actual job (Steam re-pulling content) had silently failed.

Phase 4 closes that gap. Callers (GUI / CLI / library) get an honest "Succeeded" count that reflects what actually landed on disk, plus an explicit action item for the user when Steam ignored the nudge.

### Implementation notes

- `RefreshAsync` got two new optional parameters: `TimeSpan? verifyTimeout` (default 30 s; pass `TimeSpan.Zero` to opt out) and `Func<string, bool>? contentExists` (injectable for tests).
- Three new step stages: `VerifyStart`, `VerifyOk`, `VerifyFailed`.
- Polling cadence is 500 ms; the watcher drops items from its dict as they verify so a partial-success batch finishes early.
- Two new unit tests cover both the happy and timeout paths via the injectable `contentExists` callback. Existing tests opt out via `verifyTimeout: TimeSpan.Zero` so the suite still runs in ~1 s.

### Followups not in this release

- **Steam-running precheck** before phase 3 — bail with a clear error if `steam.exe` isn't in the process list, since the steam:// URL is much more likely to land when Steam is already running. Considered but deferred so this release stays scope-tight on verification.
- **Programmatic re-subscribe** — the only fully reliable trigger for a re-pull. Needs either a logged-in browser session or a Steam Web API key. Tracked for v0.3.x.

## [0.2.1] — 2026-05-18

Smoke-test release verifying the v0.2.0 self-update pipeline end-to-end. No code changes vs. v0.2.0 beyond the version bump — the value of this release is purely existing as a "newer version" for v0.2.0 installs to detect, download, verify, and install. Future patches go on top.

### Verified

- v0.2.0 installs detect v0.2.1 via the GitHub Releases poll (footer cue + top banner in the GUI; `selfupdate` exit-10 over CLI).
- `UpdateInstaller.DownloadAndSwapAsync` downloads the v0.2.1 asset, SHA256-verifies against the GitHub-published digest, renames the running exe to `.old`, and slots the new binary into place.
- `Program.CleanupStaleArtifacts` removes the `.old` sibling on the next launch.

## [0.2.0] — 2026-05-18

In-app self-update. When a newer build is available on GitHub, Workshop Sentinel says so — once via a colored stripe across the top of the window with "Update now" / "Later", and again as a subtle footer cue that stays visible after the banner is dismissed. One click downloads the new exe, verifies its SHA256 against the digest GitHub publishes for the release asset, swaps it in alongside the running exe, and restarts. A new `selfupdate` CLI verb makes the same thing work over SSH.

### Added

- **Top-of-window update banner** (`MainWindow.xaml` + `MainWindow.xaml.cs`) — colored stripe with `Workshop Sentinel v{X} is available.` plus "Update now" / "Later" buttons. Session-dismissible (`_updateBannerDismissed`). The footer cue stays as a tertiary indicator for users who dismiss the banner but want a persistent reminder. Banner and footer share `_pendingUpdate` and a single `OnUpdateClicked` handler — install logic isn't duplicated.
- **`selfupdate` CLI verb** (`Cli/Verbs/SelfUpdateCommand.cs`) — `workshop-sentinel selfupdate [--yes] [--json]`. Exit codes: `0` latest, `0` updated, `10` update available without `--yes` (so a wrapper script can decide), `1` failed. After a successful headless install the process exits without relaunching — designed for the PC-B-over-SSH install path. Wired into `CliRunner` + `help`.
- **`Services/UpdateChecker.cs`** — polls `https://api.github.com/repos/Ensrick/workshop-sentinel/releases/latest`, picks the `WorkshopSentinel.exe` asset, parses `tag_name` / `browser_download_url` / `size` / `digest`. Result is disk-cached at `%APPDATA%\WorkshopSentinel\update-cache.json` for 6h to avoid hammering the API. SemVer compare with a special case: same numeric prefix where the running version has a `-prerelease` suffix and the latest doesn't counts as "update available" (so 0.1.0-alpha sees 0.1.0 as an upgrade).
- **`Services/UpdateInstaller.cs`** — downloads the new exe to `WorkshopSentinel.exe.new` next to the running binary, hashes it via streaming `SHA256`, verifies against the expected digest, then uses the Windows rename-running-exe trick (`File.Move` running.exe → `.old`, `.new` → running.exe) to slot the new build in. Reports byte-progress to a caller-supplied `IProgress<double>`. Cleans up partial `.new` files on failure.
- **`Program.CleanupStaleArtifacts`** — every launch best-effort-deletes leftover `WorkshopSentinel.exe.old` (from a clean prior update) and `.new` (from one that crashed mid-flight) so the install directory doesn't accumulate cruft.
- **`Program.Version`** bumped from `0.1.0-alpha` to `0.2.0`.

### Tooling

- **`release.ps1`** — full publish-to-GitHub automation. Validates the version, bumps `Program.Version`, prepends a CHANGELOG stub when needed, runs `publish.ps1` (tests + build), commits, tags `vX.Y.Z`, pushes, creates the GitHub release with the exe attached, and sanity-checks that the uploaded asset got a `digest: "sha256:..."` field from GitHub — without that, `UpdateChecker` can't verify the download and self-update silently breaks. Supports `-DryRun` for local rehearsal.

## [0.1.0-alpha] — 2026-05-17

First user-installable alpha. End-to-end audit + refresh + friend-compare flow works for public + the manual paths around friends-only items.

### Added

- **Games tab** — every installed game with Workshop subscriptions, with per-game subscribed / stale counts. Double-click a game to drill into the My Mods tab.
- **My Mods tab** — per-game audit grid with status icons (✓ Current / ⚠ Stale / ? Unknown / ✘ Removed) and Local / Live / Δ / Size columns. Per-row Refresh button plus four bulk actions: Refresh selected, Refresh ALL stale, Refresh ALL friends-only, Refresh EVERY mod (nuclear).
- **Compare to Friends tab** — left-panel Steam friends picker (auto-loaded from `localconfig.vdf`) with ★ favorite toggle + 🟢/🔵/○ online-status dots. Sort order: favorites → in-game → online → unknown → offline → alphabetical. `+` per row adds the friend as a comparison column. Right panel renders a mods × friends matrix (✓ = subscribed, · = not). A free-form input still accepts SteamID64 / vanity slug / profile URL for non-friends.
- **RefreshExecutor** (`Services/RefreshExecutor.cs`) — the destructive "hard reset" path. Per item: deletes `steamapps/workshop/content/<appid>/<itemid>/`, atomically rewrites `appworkshop_<appid>.acf` with the item entry stripped, emits `steam://workshop_download_item/<appid>/<itemid>`. ACF rewrite is staged to a `.tmp` sibling + `File.Replace`-swapped, with mid-edit detection (mtime delta) that aborts if Steam touched the file mid-flight.
- **`AcfNode.Write` + `AcfNode.Remove`** — symmetric serializer + mutator on top of the existing parser. Round-trip preserves all data, escapes `"` and `\` in values.
- **`SteamProcessGuard`** — detects whether `steam.exe` is running so the refresh flow can warn the user before deleting files Steam may hold open.
- **`FriendSubscriptionsClient`** — public-profile scraper. Resolves SteamID64 / vanity / URL → canonical SteamID64 via the XML profile endpoint, paginates `myworkshopfiles?section=mysubscriptions&appid=<n>&p=<page>`, extracts published-file IDs. Detects private profiles by phrase match.
- **`SteamFriendsResolver`** — parses the `"friends"` block of `Steam\userdata\<accountid32>\config\localconfig.vdf`. Friends are keyed by accountid32; SteamID64 reconstructed by adding `0x0110000100000000`. Self-entry skipped via the directory's own accountid.
- **Per-friend online-status fetcher** — `FetchOnlineStateAsync` parses `<onlineState>` (online / in-game / offline) from the same XML endpoint. UI scrapes up to 8 concurrent on "Refresh online".
- **App-side friend favorites** — ★ toggle persists to `%APPDATA%\WorkshopSentinel\settings.json` (`favorite_friend_steamids` array). Survives restarts.
- **Dark theme** — full custom WPF templates for `ComboBox` / `ComboBoxItem` / `TabItem` / `ListBox` / `ListBoxItem` so dropdown popups + selected tabs render on the dark palette instead of the default near-white `SystemColors.WindowBrush`.
- **CLI** — `audit [--game <appid>] [--stale-only] [--json]`, `list [--game <appid>] [--json]`, `help`. Headless banner auto-suppresses when `--json` is anywhere in argv so output pipes cleanly into `ConvertFrom-Json` / `jq`.

### Known limitations

- Friends-only / private Workshop items return `result=9` from `ISteamRemoteStorage/GetPublishedFileDetails` (the no-API-key endpoint). They surface as `Status=Unknown` and the staleness check can't run automatically. Use **Refresh ALL friends-only** to brute-force pull them. Authenticated `IPublishedFileService/GetDetails` path is planned for v0.2.0.
- Steam's per-friend "favorite" flag isn't reachable from any plain-text VDF on disk — modern Steam keeps that in the Friends UI's Chromium IndexedDB. Workshop Sentinel's ★ is its own app-side favorite list.
- No automatic Steam friends-list enumeration over the network — the local `localconfig.vdf` roster is what we use. Friends not in your local Steam (e.g. someone you added on a different PC and haven't synced) won't appear; paste their ID/vanity into the manual input instead.
- `refresh`, `doctor` CLI verbs and the API-key path are still unimplemented (see `IMPLEMENTATION.md` M5 / M6).

### Tested

- 95 unit tests passing (xUnit) covering ACF parse + write + mutate, RefreshExecutor (groups by appid, atomic rewrite, missing-ACF error path), SteamProcessGuard, FriendSubscriptionsClient (identity parsing + pagination + private-profile detection + online-state regex), SteamFriendsResolver (skips self, ignores scalar siblings, accountid → SteamID64 conversion).
- End-to-end smoke test against this machine's 84 VT2 Workshop subs and against PC-B's 53-sub install.

## [0.0.0] — 2026-05-15

Initial scaffold (M0 + M1 + M2): project structure, ACF parser, Steam path resolver, library-folders resolver, app-name resolver, Steam Web API client, staleness auditor, CLI verbs `list` / `audit` / `help`. No GUI, no refresh, no friend compare.
