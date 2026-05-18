# release.ps1 — cut a tagged GitHub release of Workshop Sentinel.
#
# Steps: bump Program.Version, prepend a CHANGELOG section if missing, run
# publish.ps1 (tests + self-contained build), commit + tag + push, then
# `gh release create` the exe and verify the asset's GitHub-computed `digest`
# field is present (UpdateChecker.cs reads it as the SHA256 for self-update).
#
# Usage:
#   .\release.ps1 0.2.0            # full release
#   .\release.ps1 -Version 0.2.0
#   .\release.ps1 0.2.0 -DryRun    # everything except push + gh release create
#
# Requires: PowerShell 5.1+, git, gh (auth'd for Ensrick/workshop-sentinel),
# dotnet SDK 9.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $root

function Write-Step($msg) { Write-Host "[release] $msg" -ForegroundColor Cyan }
function Write-OK  ($msg) { Write-Host "[release] $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "[release] $msg" -ForegroundColor Yellow }
function Write-Err ($msg) { Write-Host "[release] $msg" -ForegroundColor Red }

try {
    # ---- 1. validate version ---------------------------------------------------
    # SemVer-ish: major.minor.patch with an optional -prerelease suffix.
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9\.-]+)?$') {
        throw "Version '$Version' is not a valid SemVer (expected MAJOR.MINOR.PATCH[-pre])"
    }
    $tag = "v$Version"
    Write-Step "Preparing release $tag (DryRun=$DryRun)"

    # ---- 2. preflight: clean tree, on main, tag doesn't exist ------------------
    $status = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw "git status failed" }
    if ($status) {
        Write-Warn "Working tree has uncommitted changes — they WILL be included in the release commit:"
        $status | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "git rev-parse failed" }
    if ($branch -ne 'main') {
        Write-Warn "Current branch is '$branch', not 'main'. Continuing anyway."
    }

    $existingTag = git tag --list $tag
    if ($existingTag) { throw "Tag $tag already exists locally. Delete it first if you mean to re-cut." }
    # Remote check is best-effort — gh release create will fail loudly anyway.
    $remoteTag = git ls-remote --tags origin "refs/tags/$tag" 2>$null
    if ($remoteTag) { throw "Tag $tag already exists on origin. Bump the version or delete the remote tag." }

    # ---- 3. bump Program.Version ----------------------------------------------
    $programCs = Join-Path $root 'Program.cs'
    if (-not (Test-Path $programCs)) { throw "Program.cs not found at $programCs" }
    $progText = [System.IO.File]::ReadAllText($programCs, [System.Text.Encoding]::UTF8)
    $verRegex = 'public const string Version = "[^"]+";'
    $verReplace = "public const string Version = `"$Version`";"
    if ($progText -notmatch $verRegex) {
        throw "Could not find 'public const string Version' line in Program.cs"
    }
    $newProgText = [System.Text.RegularExpressions.Regex]::Replace($progText, $verRegex, $verReplace)
    if ($newProgText -eq $progText) {
        Write-OK "Program.Version is already $Version — no change."
    } else {
        [System.IO.File]::WriteAllText($programCs, $newProgText, (New-Object System.Text.UTF8Encoding $false))
        Write-OK "Program.Version set to $Version"
    }

    # ---- 4. ensure CHANGELOG has a [Version] section --------------------------
    $changelog = Join-Path $root 'CHANGELOG.md'
    if (-not (Test-Path $changelog)) { throw "CHANGELOG.md not found at $changelog" }
    $clText = [System.IO.File]::ReadAllText($changelog, [System.Text.Encoding]::UTF8)
    $today  = (Get-Date).ToString('yyyy-MM-dd')
    $headingPattern = "## \[$([Regex]::Escape($Version))\]"
    if ($clText -match $headingPattern) {
        Write-OK "CHANGELOG already has a [$Version] section."
    } else {
        Write-Warn "CHANGELOG has no [$Version] section — prepending a stub. Edit it before commit."
        $stub = "## [$Version] $([char]0x2014) $today`r`n`r`n_TODO: describe what changed._`r`n`r`n"
        # Insert the stub above the first existing version heading so the top-of-file
        # intro paragraph stays put.
        $firstHeadingIdx = $clText.IndexOf("`n## [")
        if ($firstHeadingIdx -lt 0) {
            $clText = $clText.TrimEnd() + "`r`n`r`n" + $stub
        } else {
            $insertAt = $firstHeadingIdx + 1
            $clText = $clText.Substring(0, $insertAt) + $stub + $clText.Substring($insertAt)
        }
        [System.IO.File]::WriteAllText($changelog, $clText, (New-Object System.Text.UTF8Encoding $false))
    }

    # ---- 5. extract release notes from CHANGELOG ------------------------------
    # Grab everything from `## [X.Y.Z]` up to the next `## [` heading.
    $notesMatch = [Regex]::Match(
        $clText,
        "## \[$([Regex]::Escape($Version))\][^\n]*\n(?<body>.*?)(?=\n## \[|\z)",
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $releaseNotes = if ($notesMatch.Success) { $notesMatch.Groups['body'].Value.Trim() } else { '' }
    if (-not $releaseNotes) {
        Write-Warn "Could not extract release notes from CHANGELOG; will fall back to --notes-from-tag."
    }

    # ---- 6. run publish.ps1 (tests + build) -----------------------------------
    Write-Step 'Running publish.ps1 (tests + Release build)...'
    & (Join-Path $root 'publish.ps1') -SkipOpen
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed (exit $LASTEXITCODE)" }

    $exe = Join-Path $root 'bin\Release\net9.0-windows\win-x64\publish\WorkshopSentinel.exe'
    if (-not (Test-Path $exe)) { throw "Built exe not found at $exe" }
    $sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-OK "Built $exe ($sizeMb MB)"

    # ---- 7. commit + tag (always local; push only if not -DryRun) -------------
    Write-Step 'Staging Program.cs + CHANGELOG.md'
    git add Program.cs CHANGELOG.md
    if ($LASTEXITCODE -ne 0) { throw "git add failed" }

    # Allow an empty commit if version + changelog were already correct, so we
    # always have a release commit to tag. (Empty diffs happen when re-cutting.)
    $cached = git diff --cached --name-only
    if ($cached) {
        git commit -m "Release $tag"
        if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
        Write-OK "Committed release $tag"
    } else {
        Write-Warn "Nothing to commit (Program.cs + CHANGELOG already on $Version). Tagging current HEAD."
    }

    git tag -a $tag -m "Release $tag"
    if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
    Write-OK "Created annotated tag $tag"

    if ($DryRun) {
        Write-Warn "[DryRun] Skipping: git push origin main, git push origin $tag, gh release create."
        Write-Warn "[DryRun] Roll back with: git tag -d $tag ; git reset --hard HEAD~1 (if a commit was made)"
        Write-OK "Dry run complete."
        return
    }

    # ---- 8. push -------------------------------------------------------------
    Write-Step "Pushing main + $tag to origin"
    git push origin $branch
    if ($LASTEXITCODE -ne 0) { throw "git push origin $branch failed" }
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "git push origin $tag failed" }

    # ---- 9. gh release create ------------------------------------------------
    Write-Step "Creating GitHub release $tag with WorkshopSentinel.exe"
    $repo = 'Ensrick/workshop-sentinel'
    if ($releaseNotes) {
        $notesFile = New-TemporaryFile
        try {
            [System.IO.File]::WriteAllText($notesFile.FullName, $releaseNotes, (New-Object System.Text.UTF8Encoding $false))
            gh release create $tag $exe --title $tag --notes-file $notesFile.FullName -R $repo
            if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)" }
        } finally {
            Remove-Item -LiteralPath $notesFile.FullName -ErrorAction SilentlyContinue
        }
    } else {
        gh release create $tag $exe --title $tag --notes-from-tag -R $repo
        if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit $LASTEXITCODE)" }
    }
    Write-OK "Release $tag published."

    # ---- 10. verify asset digest ---------------------------------------------
    # UpdateChecker.cs:154-158 reads the `digest` field on the GitHub asset and
    # strips the `sha256:` prefix. Missing digest => Status=CheckFailed and
    # self-update is silently broken for every downstream client.
    Write-Step 'Verifying asset digest'
    $json = gh release view $tag --json assets -R $repo
    if ($LASTEXITCODE -ne 0) { throw "gh release view failed" }
    $payload = $json | ConvertFrom-Json
    $asset = $payload.assets | Where-Object { $_.name -eq 'WorkshopSentinel.exe' } | Select-Object -First 1
    if (-not $asset) {
        throw "Release $tag has no WorkshopSentinel.exe asset — upload appears to have failed."
    }
    if ([string]::IsNullOrEmpty($asset.digest) -or -not $asset.digest.StartsWith('sha256:')) {
        Write-Err "Asset has no usable digest (was: '$($asset.digest)')."
        Write-Err "UpdateChecker.cs will set Status=CheckFailed for every client — self-update is BROKEN."
        Write-Err "Re-upload via: gh release upload $tag $exe --clobber -R $repo"
        throw "Missing or malformed digest on release asset."
    }
    Write-OK "Asset digest: $($asset.digest)"
    Write-OK "Release $tag is live: https://github.com/$repo/releases/tag/$tag"
}
finally {
    Pop-Location
}
