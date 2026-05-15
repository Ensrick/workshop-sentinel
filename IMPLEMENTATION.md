# Workshop Sentinel — Implementation Plan

Milestone-by-milestone task breakdown for `PLAN.md` v0.1.0 → v0.3.0.

Each milestone is one PR-sized chunk. Within a milestone, tasks are ordered: dependencies first, integration last. Tests written alongside each unit, not at the end.

---

## M0 — Project scaffold (compiles + tests run, no real logic)

Goal: a `dotnet build` clean and `dotnet test` green skeleton with the architectural shapes from `PLAN.md` §5.

| #     | File                                                  | What                                                              |
|-------|-------------------------------------------------------|-------------------------------------------------------------------|
| M0-1  | `workshop-sentinel.sln`                               | Solution with two projects.                                       |
| M0-2  | `WorkshopSentinel.csproj`                             | .NET 9, OutputType=Exe, UseWPF=true, single-file publish flags.   |
| M0-3  | `tests/WorkshopSentinel.Tests.csproj`                 | xUnit, references main project.                                   |
| M0-4  | `Program.cs`                                          | `IsHeadlessInvocation` branching (zero args → GUI; `--gui` → GUI; else → CLI). Mirrors VMBLauncher pattern exactly. |
| M0-5  | `App.xaml` / `App.xaml.cs`                            | WPF app entry that shows MainWindow.                              |
| M0-6  | `MainWindow.xaml` / `.cs`                             | Empty window, "Hello, Workshop Sentinel" + version label.         |
| M0-7  | `Cli/CliRunner.cs`                                    | Verb dispatcher with stub `help`. Real verbs added in M3.         |
| M0-8  | `Cli/Verbs/HelpCommand.cs`                            | Prints the canonical command set (copy/edit from PLAN §8).        |
| M0-9  | `Services/*` stubs                                    | One file per service named in PLAN §5 layout, each a `partial class` with `// TODO` body so DI wiring compiles. |
| M0-10 | `Services/SettingsStore.cs`                           | Real impl: loads/saves `%APPDATA%\WorkshopSentinel\settings.json`. Used by every later milestone. |
| M0-11 | `tests/SettingsStoreTests.cs`                         | Round-trip + missing-file + corrupt-JSON cases.                   |
| M0-12 | `publish.ps1`                                         | Run tests → release build → optional `-SkipOpen`. Mirror of VMBLauncher's. |
| M0-13 | `README.md`                                           | One-paragraph intro + link to `PLAN.md` and `IMPLEMENTATION.md`.  |

**Exit criteria:** `dotnet build` exits 0; `dotnet test` shows 3+ green tests; `bin/.../WorkshopSentinel.exe` opens a blank window when double-clicked, prints help when invoked as `WorkshopSentinel.exe help`.

---

## M1 — ACF parser + WorkshopEnumerator (read-only local audit)

Goal: at the end of this milestone, `WorkshopSentinel.exe list` walks every `appworkshop_*.acf` on the machine and prints `(appid, itemid, timeupdated)` for every subscribed item.

| #    | File                                       | What                                                                       |
|------|--------------------------------------------|----------------------------------------------------------------------------|
| M1-1 | `Services/AcfParser.cs`                    | Recursive-descent KeyValues parser. Public API: `static AcfNode Parse(string text)`; `AcfNode.this[string key]`, `.AsString()`, `.AsLong()`, `.Children`. ~100 LOC. |
| M1-2 | `tests/AcfParserTests.cs`                  | Cases: empty file, minimal `appmanifest`, real `appworkshop_552500.acf` (committed as test fixture), quoted-value with embedded backslash-quotes, nested 3 deep, missing-key tolerant access. |
| M1-3 | `Services/SteamPathResolver.cs`            | Registry lookup `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath` with `HKCU` fallback. Manual override from `SettingsStore`. Throws `SteamNotFoundException` if neither found. |
| M1-4 | `tests/SteamPathResolverTests.cs`          | Override-takes-precedence; tolerant of missing registry on dev machine.    |
| M1-5 | `Services/WorkshopEnumerator.cs`           | `IEnumerable<WorkshopItemLocal> EnumerateAll()` — `Directory.EnumerateFiles(steamapps/workshop, "appworkshop_*.acf")` → parse → flatten. Plus `EnumerateForApp(uint appId)`. |
| M1-6 | `tests/WorkshopEnumeratorTests.cs`         | Fed mock filesystem (in-memory dict of path→content); asserts the cim/wt/etc rows decode correctly. |
| M1-7 | `Services/LibraryFoldersResolver.cs`       | Parse `<Steam>\steamapps\libraryfolders.vdf` to find Workshop content on alternate drives. Defensive: if file missing, return single-root. |
| M1-8 | `tests/LibraryFoldersResolverTests.cs`     | Single-root, multi-root, malformed.                                        |
| M1-9 | `Cli/Verbs/ListCommand.cs`                 | Wires the above. Flags: `--game <appid>`, `--json`. Default output: human-readable table. |

**Exit criteria:** `WorkshopSentinel.exe list --game 552500` prints one row per VT2 subscription with `id | local_timeupdated | manifest | size` columns. No network calls yet.

---

## M2 — Steam Web API client + StalenessAuditor

Goal: `audit` verb compares local state against the live `time_updated` and prints per-item status.

| #     | File                                       | What                                                                                                                 |
|-------|--------------------------------------------|----------------------------------------------------------------------------------------------------------------------|
| M2-1  | `Services/SteamWebApiClient.cs`            | `Task<Dictionary<ulong, WorkshopItemRemote>> GetPublishedFileDetailsAsync(IEnumerable<ulong> ids, CancellationToken)`. Batches 100 IDs/request to `ISteamRemoteStorage/GetPublishedFileDetails/v1/`. Uses `IHttpClientFactory` for testability. |
| M2-2  | `Services/SteamWebApiClient.cs`            | Result-code handling: `result=1` ok, `result=9` returns `Remote(Visibility=Unknown, ApiResult=9)`, others logged.    |
| M2-3  | `Services/SteamWebApiClient.cs`            | Retry policy: 3 attempts with exponential backoff on 429/5xx; surface other failures as `ApiFailed`.                 |
| M2-4  | `tests/SteamWebApiClientTests.cs`          | Mock `HttpMessageHandler`. Cases: 100-ID batch happy path; 101-ID input → 2 requests; 429 → retry → 200; 500 5x → fail; mixed result codes (1, 9, others). |
| M2-5  | `Services/AppNameResolver.cs`              | Reads `appmanifest_<appid>.acf` (already on disk for every installed game) → returns `name` field. Falls back to `"App {appid}"` if not installed. |
| M2-6  | `tests/AppNameResolverTests.cs`            | VT2 manifest (real fixture) → "Vermintide 2"; missing manifest → fallback.                                            |
| M2-7  | `Services/StalenessAuditor.cs`             | Pure function: `AuditedItem Audit(WorkshopItemLocal, WorkshopItemRemote?, string? gameName)`. Status logic per PLAN §3. |
| M2-8  | `tests/StalenessAuditorTests.cs`           | local=remote → Current; local<remote → Stale; remote null → Unknown; remote.Visibility=Private → Unknown with note; banned → Removed. |
| M2-9  | `Cli/Verbs/AuditCommand.cs`                | Glue: enumerate → batch fetch → audit → render. Flags: `--game`, `--stale-only`, `--json`. |
| M2-10 | `tests/EndToEnd/AuditFixtureTests.cs`      | One real fixture (cim public + wt friends-only) → asserts cim is "Current" or "Stale" depending on timestamps, wt is "Unknown" (visibility=1, result=9). Uses live API — gated behind `--filter Category!=Live` for normal runs. |

**Exit criteria:** `WorkshopSentinel.exe audit --game 552500` runs, prints status for every VT2 subscription, marks friends-only items as "Unknown — needs API key (v0.2.0)".

---

## M3 — GUI integration

Goal: same audit but in the WPF window from PLAN §7.

| #    | File                                                    | What                                                                                                |
|------|---------------------------------------------------------|-----------------------------------------------------------------------------------------------------|
| M3-1 | `MainWindow.xaml`                                       | DataGrid with columns from PLAN §7. Bound to `ObservableCollection<AuditedItemViewModel>`.          |
| M3-2 | `MainWindow.xaml.cs`                                    | "Refresh audit" button → background `Task` → updates `ObservableCollection` on UI thread.            |
| M3-3 | `ViewModels/AuditedItemViewModel.cs`                    | Wraps `AuditedItem` + derived display props (`LocalRelative`, `RemoteRelative`, `DeltaText`, `StatusIcon`). |
| M3-4 | `Views/SettingsDialog.xaml`                             | Steam path override + (placeholder) API key input.                                                  |
| M3-5 | `tests/ViewModels/AuditedItemViewModelTests.cs`         | Relative-time formatting (`5min ago`, `2d ago`, `>365d ago`).                                       |
| M3-6 | Status bar                                              | "{total} subscribed · {stale} stale · {unknown} unknown · {removed} removed".                       |
| M3-7 | Filter dropdown                                         | `All / Stale only / Unknown only / Removed only`. Bound to `ICollectionView`.                       |
| M3-8 | Per-row context menu                                    | "Open in Steam" → `steam://url/CommunityFilePage/<id>`; "Open content folder" → `explorer.exe`.     |

**Exit criteria:** GUI launches, "Refresh audit" populates the grid in a few seconds, filter + sort work, context menu opens Steam.

---

## M4 — Refresh executor (the actually-destructive bit)

Goal: "Refresh selected" button is wired to the hard-reset flow from PLAN §2.3 option B.

| #    | File                                                    | What                                                                                                |
|------|---------------------------------------------------------|-----------------------------------------------------------------------------------------------------|
| M4-1 | `Services/RefreshExecutor.cs`                           | `Task RefreshAsync(IEnumerable<WorkshopItemLocal>, IProgress<RefreshStep>, CancellationToken)`. Per item: (1) check Steam process running, (2) delete `workshop/content/<appid>/<itemid>/`, (3) atomically rewrite `appworkshop_<appid>.acf` with the item entry removed, (4) emit `steam://workshop_download_item/<appid>/<itemid>`. |
| M4-2 | `tests/RefreshExecutorTests.cs`                         | Filesystem-mocked: per-step verification, ACF re-serialization round-trips through parser, abort if Steam writes to ACF mid-edit (re-read + diff). |
| M4-3 | `Views/ConfirmRefreshDialog.xaml`                       | Lists item titles + total bytes about to be deleted. Two-step confirm: checkbox + button. |
| M4-4 | `MainWindow.xaml.cs`                                    | "Refresh selected" toolbar button → opens dialog → runs `RefreshExecutor` with progress bar.        |
| M4-5 | `Services/SteamProcessGuard.cs`                         | Detects if `steam.exe` is running; offers "I'll close it" guidance in dialog. No auto-kill.         |
| M4-6 | `tests/SteamProcessGuardTests.cs`                       | Mocked `Process.GetProcessesByName`.                                                                |
| M4-7 | ACF re-serializer in `AcfParser.cs`                     | Symmetric `Write(AcfNode, TextWriter)` so we can edit-in-place. Tested with round-trip fixture.     |

**Exit criteria:** Select a stale item → click Refresh selected → confirm dialog → log streams progress → item disappears from `appworkshop_<appid>.acf` → restart Steam → fresh download starts.

---

## M5 — Headless CLI parity

Goal: every GUI action has a CLI equivalent. Same exit codes as VMBLauncher.

| #    | File                                       | What                                                            |
|------|--------------------------------------------|-----------------------------------------------------------------|
| M5-1 | `Cli/Verbs/RefreshCommand.cs`              | `refresh <item-id> [<item-id>...] [--yes]`. `--yes` skips confirm. |
| M5-2 | `Cli/Verbs/DoctorCommand.cs`               | Diagnostics: Steam path, registry key, libraryfolders.vdf, API reachability (ping ct), settings file readable. |
| M5-3 | `Cli/CliRunner.cs`                         | Exit-code surface: 0/1/2/3 per PLAN §8. `--no-banner`, `--config`, `--gui` global flags. |
| M5-4 | `tests/Cli/CliExitCodeTests.cs`            | Smoke test: every verb returns the documented exit code under happy + unhappy paths. |
| M5-5 | `tests/headless_smoke.ps1`                 | End-to-end against the published binary: `list`, `audit --json` parseable, `doctor`, exit-code through cmd pipe. |

**Exit criteria:** `workshop-sentinel doctor` returns 0 on this machine; `workshop-sentinel refresh <id> --yes` deletes a single item's local cache and exits 0.

---

## M8 — Friend-compare (v0.6.0+)

Goal: diff your subscription list against a friend's, optionally bulk-subscribe to what they have that you don't.

Three layered approaches, in order of complexity. Ship them in this order:

### M8a — File-exchange (MVP, zero auth)

The friend exports a snapshot, you import + diff. Works with no Steam auth at all.

- New verb `workshop-sentinel export [--game <appid>] [--out <path>]` — writes a JSON containing every `WorkshopItemLocal` enumerated from their machine. Default path: `%USERPROFILE%\Documents\workshop-sentinel-export-<hostname>-<timestamp>.json`. Includes a schema-version field for forward compat.
- New verb `workshop-sentinel diff <exported.json> [--game <appid>]` — prints three sections:
  - **They have / you don't** — list of `(publishedfileid, title-from-API, size)`
  - **You have / they don't** — same
  - **Both have, version differs** — `(publishedfileid, your_timeupdated, their_timeupdated, Δ)`
- New verb `workshop-sentinel subscribe <publishedfileid> [<publishedfileid>...] [--yes]` — emits `steam://workshop_subscribe/<appid>/<id>` per item (requires Steam running). Confirm dialog in GUI; `--yes` skips in CLI. Note: this URL handler needs verification — Steam may or may not honor it; verified subscribe paths are `steam://CommunityFilePage/<id>` (opens page, manual subscribe required) and `ISteamUGC::SubscribeItem` (game-side SDK only). MVP may have to fall back to "open page, you click subscribe."
- GUI: new "Compare to friend…" menu item → file-picker → side-by-side diff view with checkboxes → "Subscribe selected" button.

### M8b — Public-profile scrape (v0.7.0+)

If the friend's Steam profile is public AND their game details are public, their subscriptions page is renderable at `steamcommunity.com/profiles/<steamid64>/myworkshopfiles?section=mysubscriptions&appid=<appid>`. Parse the published-file IDs out of the HTML.

- New verb `workshop-sentinel diff --friend <steamid-or-vanity> [--game <appid>]` — fetch + parse + diff.
- Caveats: requires their Workshop visibility to be public; Steam paginates at 30 items/page so pagination needs handling; HTML structure can drift (low-priority brittle-ness).

### M8c — Authenticated API (v0.8.0+ or never)

If both parties have Steam Web API keys, `IPublishedFileService/GetUserFiles` returns a user's own files (uploads, not subs — different surface). Subscriptions specifically aren't in the public API surface even with auth — Valve treats them as user-private. **Most likely this milestone gets cancelled in favour of the file-exchange path.**

Risk to flag if/when we build M8: **Steam may not honor `steam://workshop_subscribe`** — if so, the "bulk subscribe" UX degrades to "open N Workshop pages, click Subscribe N times." Mitigation: in M8a, verify the URL handler against one item before claiming the verb works.

---

## M6 — API-key path (v0.2.0)

Defer the rest of the milestones to a follow-up doc once M0–M5 are landed. Sketch:

- `SteamWebApiClient.UseApiKey(string)` switches to `IPublishedFileService/GetDetails` for private/friends-only items.
- Settings dialog adds a masked text field + "Test" button (hits a known-private item).
- `AuditedItem.Status` resolution prefers `IPublishedFileService` data when available, falls back to `ISteamRemoteStorage`.

---

## Build/test commands (canonical)

Mirror VMBLauncher's:

```powershell
cd C:\Users\danjo\source\repos\workshop-sentinel
dotnet test                                  # unit tests
.\publish.ps1                                # tests + release build, opens output
.\publish.ps1 -SkipOpen                      # tests + release build
.\tests\headless_smoke.ps1                   # end-to-end against Debug build
```

## Conventions

- One service per file. `Services/<X>.cs` is `class X` (or `static class X`). Public API surface at top of file, private helpers below.
- Tests parallel sources: `Services/AcfParser.cs` → `tests/AcfParserTests.cs`. One xUnit `[Fact]` per scenario; `[Theory]` only when the scenarios literally share a body.
- No DI container. Constructor injection by hand. `Program.cs` composes the object graph in ~20 lines.
- Async everywhere a `Task<T>` exists. Never `.Result` / `.Wait()`. UI callbacks marshal via `Dispatcher.Invoke`.
- `ConfigureAwait(false)` on every library `await`; UI code in `MainWindow.xaml.cs` is the exception.
- Comments: only the WHY. Code says the WHAT.

## Out of scope (do NOT build into v0.1)

- Trimming the binary. ~60 MB self-contained is fine, matches VMBLauncher.
- Code signing. SmartScreen warning is acceptable for personal use.
- Auto-update. `publish.ps1` overwrites `bin/Release/...`; user manually copies.
- Multi-user support. Single-user, single-machine.
- Localization. English only.
