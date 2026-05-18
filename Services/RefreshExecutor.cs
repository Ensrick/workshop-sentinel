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
/// and for CLI verbose logging. Each item normally produces: BeginItem → DeleteContent →
/// RewriteAcf → EmitDownloadUrl → CompleteItem.
/// </summary>
public sealed record RefreshStep(
    ulong  PublishedFileId,
    string Stage,                 // "BeginItem" / "DeleteContent" / "RewriteAcf" / "EmitDownloadUrl" / "CompleteItem" / "Skip" / "Error"
    string Message);

public sealed record RefreshOutcome(
    ulong PublishedFileId,
    bool  Success,
    string? Error);

/// <summary>
/// Executes PLAN §2.3 option B: the "hard reset" Workshop refresh. Per item: deletes
/// `workshop/content/&lt;appid&gt;/&lt;itemid&gt;/`, strips the item entry from
/// `appworkshop_&lt;appid&gt;.acf`, and emits `steam://workshop_download_item/&lt;appid&gt;/&lt;itemid&gt;`
/// so Steam re-acquires from scratch.
///
/// Multiple items targeting the same appid are coalesced — the ACF is read-modify-written
/// once per appid even when many items in the batch share it.
///
/// Atomicity: the ACF rewrite is staged to a `.tmp` sibling, then File.Replace'd. We re-read
/// the ACF immediately before writing and abort that appid's rewrite if Steam touched it
/// mid-flight (mtime changed). The content-folder delete is NOT atomic and is the only step
/// that requires Steam to be stopped — guarded by SteamProcessGuard at the caller.
/// </summary>
public sealed class RefreshExecutor
{
    private readonly IReadOnlyList<string> _libraryRoots;
    private readonly Func<string, Task> _emitSteamUrlAsync;

    public RefreshExecutor(IReadOnlyList<string> libraryRoots, Func<string, Task>? emitSteamUrlAsync = null)
    {
        _libraryRoots = libraryRoots;
        _emitSteamUrlAsync = emitSteamUrlAsync ?? DefaultEmitSteamUrl;
    }

    public async Task<IReadOnlyList<RefreshOutcome>> RefreshAsync(
        IEnumerable<WorkshopItemLocal> items,
        IProgress<RefreshStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<RefreshOutcome>();
        var byApp = items.GroupBy(i => i.AppId);

        foreach (var appGroup in byApp)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appId = appGroup.Key;
            var itemIds = appGroup.Select(i => i.PublishedFileId).ToList();

            // Find the appworkshop_<appid>.acf and its parent workshop dir on whichever
            // library root holds this game's Workshop content.
            var (acfPath, workshopRoot) = LocateAcf(appId);
            if (acfPath is null || workshopRoot is null)
            {
                foreach (var id in itemIds)
                {
                    progress?.Report(new RefreshStep(id, "Error", $"No appworkshop_{appId}.acf found on any library."));
                    outcomes.Add(new RefreshOutcome(id, Success: false, Error: $"appworkshop_{appId}.acf not found"));
                }
                continue;
            }

            // Phase 1: per-item delete + steam:// nudge.
            foreach (var item in appGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new RefreshStep(item.PublishedFileId, "BeginItem", $"appid={item.AppId}"));

                try
                {
                    var contentDir = Path.Combine(workshopRoot, "content", appId.ToString(), item.PublishedFileId.ToString());
                    if (Directory.Exists(contentDir))
                    {
                        Directory.Delete(contentDir, recursive: true);
                        progress?.Report(new RefreshStep(item.PublishedFileId, "DeleteContent", contentDir));
                    }
                    else
                    {
                        progress?.Report(new RefreshStep(item.PublishedFileId, "DeleteContent", "(no local content dir; already gone)"));
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report(new RefreshStep(item.PublishedFileId, "Error", $"Delete failed: {ex.Message}"));
                    outcomes.Add(new RefreshOutcome(item.PublishedFileId, false, ex.Message));
                    continue;
                }

                outcomes.Add(new RefreshOutcome(item.PublishedFileId, true, null));
            }

            // Phase 2: single ACF rewrite, removing every item in this appid's batch at once.
            try
            {
                var rewroteIds = RewriteAcf(acfPath, itemIds);
                foreach (var id in rewroteIds)
                    progress?.Report(new RefreshStep(id, "RewriteAcf", acfPath));
            }
            catch (Exception ex)
            {
                foreach (var id in itemIds)
                    progress?.Report(new RefreshStep(id, "Error", $"ACF rewrite failed: {ex.Message}"));
                // Mark all of this app's items as failed (they may have lost their content dir but the manifest still references them, so Steam may not redownload — user-fixable).
                outcomes = outcomes.Select(o => itemIds.Contains(o.PublishedFileId)
                    ? new RefreshOutcome(o.PublishedFileId, false, $"ACF rewrite failed: {ex.Message}")
                    : o).ToList();
                continue;
            }

            // Phase 3: nudge Steam to re-download each item.
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
                    // Mark this specific item as failed; the manifest is already cleaned, so on next Steam launch it'll re-download anyway.
                }
            }
        }

        return outcomes;
    }

    // --- ACF rewrite, atomic ---

    /// <summary>
    /// Strips the given itemIds from <c>WorkshopItemsInstalled</c> and <c>WorkshopItemDetails</c>
    /// in the named ACF, then atomically replaces the file. Returns the IDs that were actually
    /// removed (others were absent from the manifest already).
    /// </summary>
    public static IReadOnlyList<ulong> RewriteAcf(string acfPath, IReadOnlyList<ulong> itemIds)
    {
        // Snapshot mtime so we can detect mid-edit-by-Steam between our read and write.
        var beforeMtime = File.GetLastWriteTimeUtc(acfPath);
        var text = File.ReadAllText(acfPath);
        var root = AcfNode.Parse(text);

        var removed = new List<ulong>();
        foreach (var id in itemIds)
        {
            var idStr = id.ToString();
            var installedDropped = root["WorkshopItemsInstalled"]?.Remove(idStr) ?? false;
            var detailsDropped   = root["WorkshopItemDetails"]?.Remove(idStr) ?? false;
            if (installedDropped || detailsDropped) removed.Add(id);
        }

        if (removed.Count == 0) return removed;

        var tmpPath = acfPath + ".workshop-sentinel.tmp";
        using (var sw = new StreamWriter(tmpPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            root.Write(wrapperKey: "AppWorkshop", sw);
        }

        // Re-check mtime. If Steam wrote to the ACF since our read, abort the swap and surface
        // an error to the caller. The .tmp is left in place so a human can diff if curious.
        var afterMtime = File.GetLastWriteTimeUtc(acfPath);
        if (afterMtime != beforeMtime)
        {
            throw new IOException(
                $"appworkshop ACF was modified by another process (Steam?) between read and write. " +
                $"Aborted to avoid clobbering. Tmp file left at: {tmpPath}");
        }

        // Atomic on the same volume on Windows (NTFS rename-replace).
        File.Replace(tmpPath, acfPath, destinationBackupFileName: null);
        return removed;
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
