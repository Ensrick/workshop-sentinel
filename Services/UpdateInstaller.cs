using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace WorkshopSentinel.Services;

public sealed record UpdateInstallResult(bool Success, string? Error);

/// <summary>
/// Self-update download + swap. The Windows trick: you can rename a running .exe (Windows
/// keeps the loaded image but lets the path entry move), so we download
/// <c>WorkshopSentinel.exe.new</c> next to the running exe, verify SHA256, rename the
/// running exe to <c>WorkshopSentinel.exe.old</c>, then move <c>.new</c> into the running
/// exe's path. The caller is responsible for re-launching the new exe and shutting down.
///
/// <see cref="CleanupStaleArtifacts"/> deletes leftover <c>.old</c> (from a clean update)
/// and <c>.new</c> (from one that crashed mid-flight) at next startup.
/// </summary>
public sealed class UpdateInstaller
{
    private readonly HttpClient _http;

    public UpdateInstaller(HttpClient http) { _http = http; }

    /// <summary>
    /// Best-effort cleanup of <c>WorkshopSentinel.exe.old</c> and <c>.new</c> siblings of
    /// the running exe. Swallows errors so a locked .old (e.g. just-restarted, AV holding
    /// a handle) never blocks startup.
    /// </summary>
    public static void CleanupStaleArtifacts(string runningExePath)
    {
        TryDelete(runningExePath + ".old");
        TryDelete(runningExePath + ".new");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>
    /// Download a new exe to <c>&lt;runningExePath&gt;.new</c>, verify SHA256 when an
    /// expected hash is supplied, then rename the running exe to <c>.old</c> and slot the
    /// new file into place. On any failure the partial <c>.new</c> is deleted.
    /// </summary>
    public async Task<UpdateInstallResult> DownloadAndSwapAsync(
        string runningExePath,
        string downloadUrl,
        string? expectedSha256Hex,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var newPath = runningExePath + ".new";
        var oldPath = runningExePath + ".old";

        // Wipe leftovers from a prior failed run before we start writing.
        TryDelete(newPath);
        TryDelete(oldPath);

        try
        {
            using (var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    return new UpdateInstallResult(false, $"download failed: HTTP {(int)resp.StatusCode}");

                long? total = resp.Content.Headers.ContentLength;
                using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var dst = File.Create(newPath);
                using var sha = SHA256.Create();
                using var hashing = new CryptoStream(dst, sha, CryptoStreamMode.Write);

                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await hashing.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    if (total is > 0) progress?.Report((double)copied / total.Value);
                }
                hashing.FlushFinalBlock();
                var actualHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

                if (!string.IsNullOrEmpty(expectedSha256Hex)
                    && !string.Equals(actualHash, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(newPath);
                    return new UpdateInstallResult(false,
                        $"SHA256 mismatch (expected {Short(expectedSha256Hex)}, got {Short(actualHash)})");
                }
            }

            // Swap. On Windows the running exe can be renamed because images are loaded with
            // FILE_SHARE_DELETE; this gives us an atomic-ish transition between binaries.
            File.Move(runningExePath, oldPath);
            File.Move(newPath, runningExePath);
            return new UpdateInstallResult(true, null);
        }
        catch (Exception ex)
        {
            TryDelete(newPath);
            return new UpdateInstallResult(false, ex.Message);
        }
    }

    private static string Short(string hex) =>
        hex.Length <= 12 ? hex : hex.Substring(0, 12) + "…";
}
