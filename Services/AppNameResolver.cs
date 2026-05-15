using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace WorkshopSentinel.Services;

/// <summary>
/// Maps a Steam appid to a human-readable game name by reading
/// `appmanifest_&lt;appid&gt;.acf` from each library root. Cached after first read
/// because we look up the same handful of appids many times per audit.
///
/// Falls back to <c>"App {appid}"</c> when the manifest is missing — that's the case
/// for games the user has subbed mods for but uninstalled.
/// </summary>
public sealed class AppNameResolver
{
    private readonly IReadOnlyList<string> _libraryRoots;
    private readonly ConcurrentDictionary<uint, string> _cache = new();

    public AppNameResolver(IReadOnlyList<string> libraryRoots)
    {
        _libraryRoots = libraryRoots;
    }

    public string Resolve(uint appId) => _cache.GetOrAdd(appId, ResolveUncached);

    private string ResolveUncached(uint appId)
    {
        foreach (var root in _libraryRoots)
        {
            var path = Path.Combine(root, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(path)) continue;
            try
            {
                var node = AcfNode.ParseFile(path);
                var name = node["name"]?.AsString();
                if (!string.IsNullOrWhiteSpace(name)) return name!;
            }
            catch
            {
                // Steam wrote a partial file or it's locked — fall through to next root / fallback.
            }
        }
        return $"App {appId}";
    }
}
