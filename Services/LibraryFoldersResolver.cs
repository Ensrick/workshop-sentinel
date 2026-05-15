using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WorkshopSentinel.Services;

/// <summary>
/// Enumerates every Steam library root on the machine. Each root has its own
/// `steamapps/workshop/appworkshop_*.acf` files, so a user with games on multiple drives
/// has multiple Workshop content trees.
///
/// Parses `&lt;Steam&gt;\steamapps\libraryfolders.vdf` (KeyValues format). Layout:
///   "libraryfolders" {
///       "0" { "path" "C:\\Program Files (x86)\\Steam"  ... }
///       "1" { "path" "D:\\SteamLibrary"  ... }
///   }
/// </summary>
public sealed class LibraryFoldersResolver
{
    public IReadOnlyList<string> Resolve(string steamInstallPath)
    {
        var vdfPath = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            // Defensive: pre-2020 Steam clients didn't have this file. Fall back to the single
            // primary install as the only library.
            return new[] { steamInstallPath };
        }

        var node = AcfNode.ParseFile(vdfPath);

        var roots = new List<string>();
        foreach (var (_, childNode) in node.Children)
        {
            // Each child is keyed "0", "1", ... — pull the "path" field if present.
            if (!childNode.IsObject) continue;
            var p = childNode["path"]?.AsString();
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            {
                roots.Add(p!);
            }
        }

        // If parsing succeeded but found no libraries (malformed file), fall back to primary.
        if (roots.Count == 0) roots.Add(steamInstallPath);

        // De-dup case-insensitively — Steam sometimes lists the primary twice in odd cases.
        return roots
            .Select(r => Path.GetFullPath(r))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
