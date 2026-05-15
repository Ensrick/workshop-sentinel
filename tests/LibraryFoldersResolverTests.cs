using System;
using System.IO;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public sealed class LibraryFoldersResolverTests : IDisposable
{
    private readonly string _steamRoot;
    private readonly string _vdfPath;

    public LibraryFoldersResolverTests()
    {
        _steamRoot = Path.Combine(Path.GetTempPath(), "WSLibTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_steamRoot, "steamapps"));
        _vdfPath = Path.Combine(_steamRoot, "steamapps", "libraryfolders.vdf");
    }

    public void Dispose()
    {
        try { Directory.Delete(_steamRoot, recursive: true); } catch { }
    }

    [Fact]
    public void Missing_vdf_falls_back_to_primary_only()
    {
        var resolver = new LibraryFoldersResolver();

        var roots = resolver.Resolve(_steamRoot);

        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(_steamRoot), roots[0]);
    }

    [Fact]
    public void Single_library_listed_in_vdf_is_returned()
    {
        File.WriteAllText(_vdfPath, $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"      "{{_steamRoot.Replace("\\", "\\\\")}}"
                    "label"     ""
                }
            }
            """);
        var resolver = new LibraryFoldersResolver();

        var roots = resolver.Resolve(_steamRoot);

        Assert.Single(roots);
        Assert.Equal(Path.GetFullPath(_steamRoot), roots[0]);
    }

    [Fact]
    public void Multiple_libraries_all_returned_skipping_nonexistent()
    {
        // Create a second real library dir; third is fake (will be skipped).
        var lib2 = Path.Combine(Path.GetTempPath(), "WSLib2_" + Path.GetRandomFileName());
        Directory.CreateDirectory(lib2);
        try
        {
            File.WriteAllText(_vdfPath, $$"""
                "libraryfolders"
                {
                    "0" { "path" "{{_steamRoot.Replace("\\", "\\\\")}}" }
                    "1" { "path" "{{lib2.Replace("\\", "\\\\")}}" }
                    "2" { "path" "Z:\\definitely-fake" }
                }
                """);
            var resolver = new LibraryFoldersResolver();

            var roots = resolver.Resolve(_steamRoot);

            Assert.Equal(2, roots.Count);
            Assert.Contains(roots, r => string.Equals(r, Path.GetFullPath(_steamRoot), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(roots, r => string.Equals(r, Path.GetFullPath(lib2),       StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(lib2, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Empty_libraryfolders_falls_back_to_primary()
    {
        File.WriteAllText(_vdfPath, """
            "libraryfolders"
            {
            }
            """);
        var resolver = new LibraryFoldersResolver();

        var roots = resolver.Resolve(_steamRoot);

        Assert.Single(roots);
    }

    [Fact]
    public void Duplicate_entries_are_de_duplicated()
    {
        File.WriteAllText(_vdfPath, $$"""
            "libraryfolders"
            {
                "0" { "path" "{{_steamRoot.Replace("\\", "\\\\")}}" }
                "1" { "path" "{{_steamRoot.Replace("\\", "\\\\")}}" }
            }
            """);
        var resolver = new LibraryFoldersResolver();

        var roots = resolver.Resolve(_steamRoot);

        Assert.Single(roots);
    }
}
