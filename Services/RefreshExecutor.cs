using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WorkshopSentinel.Services;

/// <summary>
/// Step emitted while a refresh batch runs. Surfaced via IProgress for the GUI progress bar
/// and for CLI verbose logging. Each item normally produces:
/// BeginItem → Snapshot → MutateAcf → EmitDownloadUrl → CompleteItem → VerifyStart →
/// VerifyOk (or VerifyFailed).
/// </summary>
public sealed record RefreshStep(
    ulong  PublishedFileId,
    string Stage,                 // "BeginItem" / "Snapshot" / "MutateAcf" / "EmitDownloadUrl" / "CompleteItem" / "VerifyStart" / "VerifyOk" / "VerifyFailed" / "Skip" / "Error"
    string Message);

public sealed record RefreshOutcome(
    ulong PublishedFileId,
    bool  Success,
    string? Error);

/// <summary>
/// Non-destructive Workshop refresh. Per appid: snapshots the ACF to a .bak sibling,
/// then mutates `appworkshop_&lt;appid&gt;.acf` in place so Steam's sub-monitor sees the
/// listed items as stale and re-pulls. Specifically:
/// <list type="bullet">
///   <item>Top-level `NeedsDownload` is set to `"1"`.</item>
///   <item>For each item: per-item `manifest` is set to `"-1"` and `timeupdated` to `"1"`
///   in BOTH `WorkshopItemsInstalled.&lt;id&gt;` and `WorkshopItemDetails.&lt;id&gt;`. The blocks
///   themselves stay present and `ugchandle` / `size` are left alone.</item>
/// </list>
///
/// Crucially, the local content directory `&lt;workshop&gt;/content/&lt;appid&gt;/&lt;itemid&gt;/` is
/// NEVER deleted. If Steam ignores the nudge or our verify times out, the user still has
/// their existing copy — no data loss. This is the lesson from amanda's 2026-05-19 burn,
/// where the old "delete + steam:// + pray" flow lost 20+ mods when Steam silently dropped
/// every URL. See CHANGELOG 0.3.0.
///
/// The steam:// nudge in phase 3 stays as belt-and-suspenders. It can no-op without
/// consequence because the ACF mutation already armed Steam's sub-monitor.
///
/// Atomicity: the ACF rewrite stages to a `.workshop-sentinel.tmp` sibling, mtime-guards
/// against Steam editing the file mid-flight, then `File.Replace`s atomically (NTFS rename).
/// The `.workshop-sentinel.bak` snapshot stays in place so a human can roll back if needed —
/// it's overwritten on the next refresh against the same appid.
/// </summary>
public sealed class RefreshExecutor
{
    /// <summary>
    /// Default window the verification phase waits for Steam to flip our sentinel
    /// `timeupdated="1"` / `manifest="-1"` back to real values. Empirically Steam's
    /// sub-monitor reacts within seconds when Steam is running; 60 s is the generous
    /// upper bound — the sub-monitor polls every ~10 min in the worst case (per the
    /// rFactor 2 community thread), so the realistic ceiling is "next Steam launch"
    /// rather than the 60 s window.
    /// </summary>
    public static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Stale `timeupdated` sentinel we write to force Steam's re-pull.</summary>
    public const string SentinelTimeUpdated = "1";

    /// <summary>Stale `manifest` sentinel matching Valve's own "unset" marker.</summary>
    public const string SentinelManifest = "-1";

    /// <summary>
    /// `timeupdated` is a Unix epoch seconds value. Anything below ~2020-01-01 is either
    /// our sentinel or a malformed entry, neither of which counts as "Steam re-pulled."
    /// </summary>
    private const long MinRealEpoch = 1_577_836_800;

    private readonly IReadOnlyList<string> _libraryRoots;
    private readonly Func<string, Task> _emitSteamUrlAsync;

    public RefreshExecutor(IReadOnlyList<string> libraryRoots, Func<string, Task>? emitSteamUrlAsync = null)
    {
        _libraryRoots = libraryRoots;
        _emitSteamUrlAsync = emitSteamUrlAsync ?? DefaultEmitSteamUrl;
    }

    /// <summary>
    /// Non-destructive Workshop refresh. See class-level doc for the per-item pipeline.
    /// After phase 3 (the steam:// nudge) we poll each item's <c>timeupdated</c> field in
    /// the live ACF for up to <paramref name="verifyTimeout"/> and emit <c>VerifyOk</c>
    /// as soon as Steam flips it back to a real epoch (= the re-pull landed). Items still
    /// at the sentinel value when the window elapses get <c>VerifyFailed</c> and their
    /// outcome flipped to false — but their content folder is untouched the whole time,
    /// so the user can just try again or wait for Steam's next sync.
    ///
    /// Tests can inject <paramref name="readItemTimeUpdated"/> to simulate Steam responding
    /// (or not) to the ACF mutation without actually involving Steam.
    /// </summary>
    public async Task<IReadOnlyList<RefreshOutcome>> RefreshAsync(
        IEnumerable<WorkshopItemLocal> items,
        IProgress<RefreshStep>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? verifyTimeout = null,
        Func<string, ulong, long>? readItemTimeUpdated = null)
    {
        var itemList = items.ToList();
        var outcomes = new List<RefreshOutcome>();
        var byApp = itemList.GroupBy(i => i.AppId);

        foreach (var appGroup in byApp)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appId = appGroup.Key;
            var itemIds = appGroup.Select(i => i.PublishedFileId).ToList();

            var (acfPath, _) = LocateAcf(appId);
            if (acfPath is null)
            {
                foreach (var id in itemIds)
                {
                    progress?.Report(new RefreshStep(id, "Error", $"No appworkshop_{appId}.acf found on any library."));
                    outcomes.Add(new RefreshOutcome(id, Success: false, Error: $"appworkshop_{appId}.acf not found"));
                }
                continue;
            }

            // Phase 1: snapshot the ACF before we touch it. Roll-back path if the user wants
            // to undo (or if Steam misbehaves and we need forensic evidence).
            var backupPath = acfPath + ".workshop-sentinel.bak";
            try
            {
                File.Copy(acfPath, backupPath, overwrite: true);
                foreach (var id in itemIds)
                    progress?.Report(new RefreshStep(id, "Snapshot", backupPath));
            }
            catch (Exception ex)
            {
                foreach (var id in itemIds)
                {
                    progress?.Report(new RefreshStep(id, "Error", $"ACF snapshot failed: {ex.Message}"));
                    outcomes.Add(new RefreshOutcome(id, false, $"snapshot failed: {ex.Message}"));
                }
                continue;
            }

            foreach (var id in itemIds)
                progress?.Report(new RefreshStep(id, "BeginItem", $"appid={appId}"));

            // Phase 2: single ACF mutation. All items in this appid's batch get their
            // sentinels written at once — Steam sees one atomic rewrite, not N.
            IReadOnlyList<ulong> mutated;
            try
            {
                mutated = MutateAcf(acfPath, itemIds);
                foreach (var id in mutated)
                    progress?.Report(new RefreshStep(id, "MutateAcf", acfPath));
                foreach (var id in itemIds.Except(mutated))
                    progress?.Report(new RefreshStep(id, "Skip", "item not present in appworkshop ACF; Steam already considers it absent"));
            }
            catch (Exception ex)
            {
                foreach (var id in itemIds)
                {
                    progress?.Report(new RefreshStep(id, "Error", $"ACF mutation failed: {ex.Message}"));
                    outcomes.Add(new RefreshOutcome(id, false, $"ACF mutation failed: {ex.Message}"));
                }
                continue;
            }

            // Items that weren't in the ACF count as "succeeded" — Steam will pull them on
            // next sync because they're subscribed-but-missing, exactly the state we want.
            foreach (var id in itemIds)
                outcomes.Add(new RefreshOutcome(id, Success: true, Error: null));

            // Phase 3: belt-and-suspenders steam:// nudge per item. If Steam ignores it
            // (foreground state, sub cache, etc.), the ACF mutation still triggers the
            // re-pull on next sub-monitor tick.
            foreach (var item in appGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var url = $"steam://workshop_download_item/{item.AppId}/{item.PublishedFileId}";
                    await _emitSteamUrlAsync(url).ConfigureAwait(false);
                    progress?.Report(new RefreshStep(item.PublishedFileId, "EmitDownloadUrl", url));
                    progress?.Report(new RefreshStep(item.PublishedFileId, "CompleteItem", "ok"));
                }
                catch (Exception ex)
                {
                    progress?.Report(new RefreshStep(item.PublishedFileId, "Error", $"steam:// launch failed: {ex.Message}"));
                    // Don't fail the outcome — the ACF mutation is the load-bearing trigger.
                }
            }
        }

        // Phase 4: verify Steam actually flipped the ACF entries back to real values.
        // The signal we watch is per-item `timeupdated` flipping from our sentinel "1"
        // to a real Unix epoch (>= 2020-01-01). When Steam pulls, it writes the new
        // download timestamp + the new manifest hash + the new size all together.
        var window = verifyTimeout ?? DefaultVerifyTimeout;
        if (window > TimeSpan.Zero)
        {
            await VerifyMutationLandedAsync(itemList, outcomes,
                readItemTimeUpdated ?? DefaultReadItemTimeUpdated,
                window, progress, cancellationToken).ConfigureAwait(false);
        }

        return outcomes;
    }

    /// <summary>
    /// Polls each successfully-mutated item's <c>timeupdated</c> in the live ACF until
    /// it flips back to a real epoch (= Steam re-pulled) or the timeout elapses.
    /// Items that time out get a <c>VerifyFailed</c> step and their outcome flipped to
    /// failed. The user's local content stays put either way — failure here is recoverable
    /// (try again, or wait for Steam's ~10-min sub-monitor tick).
    /// </summary>
    private async Task VerifyMutationLandedAsync(
        IReadOnlyList<WorkshopItemLocal> items,
        List<RefreshOutcome> outcomes,
        Func<string, ulong, long> readItemTimeUpdated,
        TimeSpan timeout,
        IProgress<RefreshStep>? progress,
        CancellationToken ct)
    {
        // Build the watch list: items whose phase-2 succeeded and we know the ACF path for.
        var watch = new Dictionary<ulong, (uint appId, string acfPath)>();
        foreach (var item in items)
        {
            var outcome = outcomes.FirstOrDefault(o => o.PublishedFileId == item.PublishedFileId);
            if (outcome is null || !outcome.Success) continue;
            var (acfPath, _) = LocateAcf(item.AppId);
            if (acfPath is null) continue;
            watch[item.PublishedFileId] = (item.AppId, acfPath);
            progress?.Report(new RefreshStep(item.PublishedFileId, "VerifyStart", acfPath));
        }

        if (watch.Count == 0) return;

        var deadline = DateTime.UtcNow + timeout;
        var pollInterval = TimeSpan.FromMilliseconds(500);
        while (watch.Count > 0 && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var id in watch.Keys.ToList())
            {
                var (_, acfPath) = watch[id];
                long t;
                try { t = readItemTimeUpdated(acfPath, id); }
                catch { t = 0; }  // transient read failure (Steam editing) — try again next tick
                if (t >= MinRealEpoch)
                {
                    progress?.Report(new RefreshStep(id, "VerifyOk", $"timeupdated={t}"));
                    watch.Remove(id);
                }
            }
            if (watch.Count == 0) break;
            try { await Task.Delay(pollInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { throw; }
        }

        // Anything still in the watch list timed out at the sentinel.
        foreach (var kv in watch)
        {
            const string failMessage =
                "Steam did not flip this item's timeupdated back to a real value within the verification window. " +
                "Common causes: Steam wasn't running (start Steam — the ACF mutation triggers a re-pull on launch), " +
                "no internet, or Steam's sub-monitor hasn't ticked yet (~10 min worst case). " +
                "Your local content folder was NOT touched — your previous copy is still on disk. " +
                "Either wait for Steam's next sync, restart Steam, or re-run the refresh.";
            progress?.Report(new RefreshStep(kv.Key, "VerifyFailed", failMessage));
            var idx = outcomes.FindIndex(o => o.PublishedFileId == kv.Key && o.Success);
            if (idx >= 0)
            {
                outcomes[idx] = new RefreshOutcome(kv.Key, Success: false,
                    Error: "Verification timeout: Steam did not pick up the manifest mutation within the window. Local content is untouched; restart Steam or wait for the next sub-monitor tick.");
            }
        }
    }

    /// <summary>
    /// Default reader used by the verify phase. Parses the live ACF and returns the
    /// per-item <c>timeupdated</c> from <c>WorkshopItemsInstalled.&lt;id&gt;</c>, or 0 if the
    /// field is missing / unparseable. Tests inject a faster in-memory variant.
    /// </summary>
    private static long DefaultReadItemTimeUpdated(string acfPath, ulong itemId)
    {
        try
        {
            var root = AcfNode.ParseFile(acfPath);
            var entry = root["WorkshopItemsInstalled"]?[itemId.ToString()];
            return entry?["timeupdated"]?.AsLong() ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    // --- ACF mutation, atomic ---

    /// <summary>
    /// Marks the given itemIds as stale in the named ACF by setting per-item
    /// <c>timeupdated="1"</c> and <c>manifest="-1"</c> in BOTH the
    /// <c>WorkshopItemsInstalled</c> and <c>WorkshopItemDetails</c> subtrees, plus
    /// top-level <c>NeedsDownload="1"</c>. Atomically rewrites via tmp+File.Replace,
    /// with mtime-guard against mid-flight edits by Steam. Returns the IDs whose entry
    /// was found and mutated (others weren't in the manifest to begin with).
    /// </summary>
    public static IReadOnlyList<ulong> MutateAcf(string acfPath, IReadOnlyList<ulong> itemIds)
    {
        var beforeMtime = File.GetLastWriteTimeUtc(acfPath);
        var text = File.ReadAllText(acfPath);
        var root = AcfNode.Parse(text);

        // Top-level sentinel — proven trigger per the rFactor 2 community thread:
        // when NeedsDownload=1 AND per-item state looks invalid, Steam re-pulls on
        // its next sub-monitor tick (every ~10 min, or immediately on Steam launch).
        root.SetScalar("NeedsDownload", "1");

        var installed = root["WorkshopItemsInstalled"];
        var details   = root["WorkshopItemDetails"];

        var mutated = new List<ulong>();
        foreach (var id in itemIds)
        {
            var idStr = id.ToString();
            var installedEntry = installed?[idStr];
            var detailsEntry   = details?[idStr];
            var touched = false;

            if (installedEntry is not null && installedEntry.IsObject)
            {
                installedEntry.SetScalar("timeupdated", SentinelTimeUpdated);
                installedEntry.SetScalar("manifest",   SentinelManifest);
                touched = true;
            }
            if (detailsEntry is not null && detailsEntry.IsObject)
            {
                detailsEntry.SetScalar("timeupdated", SentinelTimeUpdated);
                detailsEntry.SetScalar("manifest",    SentinelManifest);
                touched = true;
            }
            if (touched) mutated.Add(id);
        }

        if (mutated.Count == 0) return mutated;

        var tmpPath = acfPath + ".workshop-sentinel.tmp";
        using (var sw = new StreamWriter(tmpPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            root.Write(wrapperKey: "AppWorkshop", sw);
        }

        var afterMtime = File.GetLastWriteTimeUtc(acfPath);
        if (afterMtime != beforeMtime)
        {
            throw new IOException(
                $"appworkshop ACF was modified by another process (Steam?) between read and write. " +
                $"Aborted to avoid clobbering. Tmp file left at: {tmpPath}");
        }

        File.Replace(tmpPath, acfPath, destinationBackupFileName: null);
        return mutated;
    }

    private (string? acfPath, string? workshopRoot) LocateAcf(uint appId)
    {
        var name = $"appworkshop_{appId}.acf";
        foreach (var root in _libraryRoots)
        {
            var workshop = Path.Combine(root, "steamapps", "workshop");
            var acf = Path.Combine(workshop, name);
            if (File.Exists(acf)) return (acf, workshop);
        }
        return (null, null);
    }

    // --- steam:// launcher ---

    private static Task DefaultEmitSteamUrl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Caller logs; swallow here so one bad URL doesn't poison the batch.
        }
        return Task.CompletedTask;
    }
}
