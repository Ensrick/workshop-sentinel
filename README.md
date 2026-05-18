# Workshop Sentinel

A small Windows app (+ headless CLI) that audits every Steam Workshop subscription on the machine — across every installed game — and tells you which items are stale relative to what's actually published. One-click "refresh" forces the listed items to re-download.

## Status

**Alpha (`v0.1.0-alpha`, 2026-05-17).** End-to-end audit + refresh + friend-compare works. Pre-1.0 — see [`CHANGELOG.md`](./CHANGELOG.md) for the full slice of what shipped and [`PLAN.md`](./PLAN.md) for the design.

## Install

Grab `WorkshopSentinel.exe` from the [latest release](https://github.com/Ensrick/workshop-sentinel/releases/latest) (single-file, ~60 MB, self-contained .NET 9 — no runtime to install).

Windows SmartScreen will warn the first time you run it (unsigned binary). Click **More info → Run anyway**.

## What it does

- **Games tab** — every installed game with Workshop subscriptions, with per-game subscribed / stale counts. Double-click to drill in.
- **My Mods tab** — per-game audit grid with status icons (✓ Current / ⚠ Stale / ? Unknown / ✘ Removed). Per-row Refresh button plus four bulk actions: Refresh selected, Refresh ALL stale, Refresh ALL friends-only, Refresh EVERY mod (nuclear). Refresh = delete the local content folder, strip the entry from `appworkshop_<appid>.acf`, fire `steam://workshop_download_item/<appid>/<itemid>`.
- **Compare to Friends tab** — left-panel friends picker loaded from your local Steam `localconfig.vdf`, ★-favorite toggle (persisted), live online-status dots. Each `+` adds a friend as a column in the comparison matrix; ✓ = subscribed, · = not.

### Before you click Refresh

**Close Steam first.** The refresh deletes files inside `steamapps\workshop\content\<appid>\<itemid>\`. If Steam is running, it may hold handles on those files and the delete will fail. Workshop Sentinel warns you and offers Cancel.

### Friends-only / private items

The public Steam Web API returns `result=9` for friends-only mods, so staleness can't be auto-detected. They show up as `Unknown` in the My Mods tab. Use **Refresh ALL friends-only** to force-pull every Unknown row — that's the right move when an author you follow has a friends-only mod you suspect is stale.

## Headless CLI

```powershell
WorkshopSentinel.exe                     # launches the GUI
WorkshopSentinel.exe --gui               # GUI even when other args are present

WorkshopSentinel.exe list                # every local sub across every game
WorkshopSentinel.exe list --game 552500  # one game only
WorkshopSentinel.exe list --json         # JSON for downstream tools

WorkshopSentinel.exe audit                          # public-API staleness check across every game
WorkshopSentinel.exe audit --game 552500            # one game only
WorkshopSentinel.exe audit --game 552500 --stale-only
WorkshopSentinel.exe audit --json                   # banner auto-suppressed for `| ConvertFrom-Json`

WorkshopSentinel.exe help
```

Exit codes: `0` ok / `1` runtime error / `2` bad usage / `3` preflight failed (Steam path missing).

`refresh` and `doctor` verbs are planned for the v0.2.0 milestone (see [`IMPLEMENTATION.md`](./IMPLEMENTATION.md) §M5).

## Build from source

```powershell
cd workshop-sentinel
dotnet test workshop-sentinel.sln       # 95 tests
.\publish.ps1                           # tests + single-file Release publish + open output folder
.\publish.ps1 -SkipOpen                 # same, no Explorer
.\publish.ps1 -SkipOpen -SkipTests      # build only
```

Output: `bin\Release\net9.0-windows\win-x64\publish\WorkshopSentinel.exe`.

Requires .NET 9 SDK. Targets `net9.0-windows` (WPF).

## Why

Steam silently caches Workshop content. Two failure modes the built-in Steam UI doesn't surface:

- **Subscriber stale** — Steam thinks your local copy is current but the author published a new revision and you didn't get it.
- **Author stale (the friend who never sees your fix)** — you uploaded, your friend's Steam never re-acquired, you spend an hour debugging an already-fixed bug on their PC.

There's a long-standing Steam feature request for a "Verify Workshop" button equivalent to "Verify integrity of game files." Never built. This is that.

## License

[MIT](./LICENSE).
