using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WorkshopSentinel.Services;

/// <summary>
/// Walks every `appworkshop_&lt;appid&gt;.acf` under the given Steam library roots and
/// yields one <see cref="WorkshopItemLocal"/> per subscribed item.
/// </summary>
public sealed class WorkshopEnumerator
{
    private static readonly Regex AcfNameRegex =
        new(@"^appworkshop_(\d+)\.acf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Enumerate every subscribed Workshop item across every library.
    /// </summary>
    /// <param name="libraryRoots">Output of <see cref="LibraryFoldersResolver"/>.</param>
    public IEnumerable<WorkshopItemLocal> EnumerateAll(IEnumerable<string> libraryRoots)
    {
        foreach (var root in libraryRoots)
        {
            var workshopDir = Path.Combine(root, "steamapps", "workshop");
            if (!Directory.Exists(workshopDir)) continue;

            foreach (var acf in Directory.EnumerateFiles(workshopDir, "appworkshop_*.acf"))
            {
                foreach (var item in ParseOne(acf))
                    yield return item;
            }
        }
    }

    /// <summary>
    /// Enumerate just the subscriptions for a single appid. Faster than EnumerateAll when
    /// caller only cares about one game.
    /// </summary>
    public IEnumerable<WorkshopItemLocal> EnumerateForApp(IEnumerable<string> libraryRoots, uint appId)
    {
        var wantName = $"appworkshop_{appId}.acf";
        foreach (var root in libraryRoots)
        {
            var acf = Path.Combine(root, "steamapps", "workshop", wantName);
            if (!File.Exists(acf)) continue;
            foreach (var item in ParseOne(acf))
                yield return item;
        }
    }

    private static IEnumerable<WorkshopItemLocal> ParseOne(string acfPath)
    {
        var fileName = Path.GetFileName(acfPath);
        var m = AcfNameRegex.Match(fileName);
        if (!m.Success || !uint.TryParse(m.Groups[1].Value, out var appId))
            yield break;

        AcfNode root;
        try { root = AcfNode.ParseFile(acfPath); }
        catch (Exception)
        {
            // Steam mid-write or genuinely corrupt — skip the file rather than fail the whole audit.
            yield break;
        }

        var installed = root["WorkshopItemsInstalled"];
        if (installed is null || !installed.IsObject) yield break;

        foreach (var (idStr, body) in installed.Children)
        {
            if (!ulong.TryParse(idStr, out var publishedFileId)) continue;
            if (!body.IsObject) continue;

            var size = body["size"]?.AsLong() ?? 0;
            var timeUpdated = body["timeupdated"]?.AsLong() ?? 0;
            var manifest = body["manifest"]?.AsString() ?? "";

            yield return new WorkshopItemLocal(
                AppId: appId,
                PublishedFileId: publishedFileId,
                LocalTimeUpdated: timeUpdated,
                LocalManifest: manifest,
                LocalSizeBytes: size);
        }
    }

    /// <summary>
    /// Test seam: enumerate from a virtual filesystem mapping `acfPath → content`. Bypasses
    /// real disk access entirely. Public so tests can call it without InternalsVisibleTo.
    /// </summary>
    public IEnumerable<WorkshopItemLocal> EnumerateFromMemory(IDictionary<string, string> acfFiles)
    {
        foreach (var (path, content) in acfFiles)
        {
            var fileName = Path.GetFileName(path);
            var m = AcfNameRegex.Match(fileName);
            if (!m.Success || !uint.TryParse(m.Groups[1].Value, out var appId)) continue;

            AcfNode root;
            try { root = AcfNode.Parse(content); }
            catch { continue; }

            var installed = root["WorkshopItemsInstalled"];
            if (installed is null || !installed.IsObject) continue;

            foreach (var (idStr, body) in installed.Children)
            {
                if (!ulong.TryParse(idStr, out var pid)) continue;
                if (!body.IsObject) continue;

                yield return new WorkshopItemLocal(
                    AppId: appId,
                    PublishedFileId: pid,
                    LocalTimeUpdated: body["timeupdated"]?.AsLong() ?? 0,
                    LocalManifest: body["manifest"]?.AsString() ?? "",
                    LocalSizeBytes: body["size"]?.AsLong() ?? 0);
            }
        }
    }
}
