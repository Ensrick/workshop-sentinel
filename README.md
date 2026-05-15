# Workshop Sentinel

A small Windows app (+ headless CLI) that audits every Steam Workshop subscription on the machine — across every installed game — and tells you which items are stale relative to what's actually published. One-click "refresh" forces the listed items to re-download.

## Status

**Pre-alpha.** Scaffold (M0) only. See [`PLAN.md`](./PLAN.md) for design, [`IMPLEMENTATION.md`](./IMPLEMENTATION.md) for milestone-by-milestone task list.

## Build

```powershell
cd C:\Users\danjo\source\repos\workshop-sentinel
dotnet test                       # run unit tests
.\publish.ps1                     # tests + release build, opens output
.\publish.ps1 -SkipOpen           # tests + release build, no explorer
```

Output: `bin\Release\net9.0-windows\win-x64\publish\WorkshopSentinel.exe`.

## Run

```powershell
WorkshopSentinel.exe              # GUI
WorkshopSentinel.exe --gui        # GUI (even with other args present)
WorkshopSentinel.exe help         # CLI help
WorkshopSentinel.exe list         # not implemented yet (M1)
WorkshopSentinel.exe audit        # not implemented yet (M2)
```

## Why

Steam silently caches Workshop content. There's no built-in way to verify your local copy matches what's currently published. This tool surfaces that comparison.
