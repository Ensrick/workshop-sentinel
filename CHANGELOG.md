# Changelog

All notable changes to Workshop Sentinel are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
