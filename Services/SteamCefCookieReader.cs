using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WorkshopSentinel.Services;

/// <summary>
/// The Steam-authenticated cookies we care about for the Workshop subscribe POST.
/// Both are required and both are extracted from Steam's CEF webhelper cookie store.
/// </summary>
public sealed record SteamSessionCookies(string SteamLoginSecure, string SessionId);

public enum SteamCefCookieOutcome
{
    Ok,
    SteamNotInstalled,        // no htmlcache at expected path
    MasterKeyDecryptFailed,   // DPAPI returned an error (different user account?)
    SqliteOpenFailed,         // DB locked or corrupt
    MissingRequiredCookies,   // steamLoginSecure or sessionid not in DB (user never logged in via Steam client?)
}

public sealed record SteamCefCookieReadResult(
    SteamCefCookieOutcome Outcome,
    SteamSessionCookies?  Cookies,
    string?               ErrorDetail);

/// <summary>
/// Extracts Steam's authenticated session cookies from the CEF webhelper's cookie store.
/// Steam's overlay/built-in browser shares this cookie jar with the user's logged-in
/// Steam account, which lets us POST to <c>steamcommunity.com/sharedfiles/subscribe</c>
/// as that user — no separate browser login required.
///
/// Pipeline (ported from `extract_steam_cookies.ps1`, verified working 2026-05-17 against PC-B):
/// <list type="number">
///   <item>Read <c>%LOCALAPPDATA%\Steam\htmlcache\Local State</c> JSON →
///   <c>os_crypt.encrypted_key</c> base64 → strip 5-byte "DPAPI" prefix →
///   <see cref="ProtectedData.Unprotect"/> with <c>CurrentUser</c> scope → 32-byte AES-256 key.</item>
///   <item>Copy <c>%LOCALAPPDATA%\Steam\htmlcache\Default\Network\Cookies</c> to a temp path
///   (the live DB may be locked by the CEF webhelper) and query
///   <c>SELECT host_key, name, encrypted_value FROM cookies WHERE host_key LIKE '%steamcommunity.com%'</c>.</item>
///   <item>For each encrypted_value, parse Chromium v10/v11 format:
///   3-byte prefix + 12-byte IV + ciphertext + 16-byte GCM tag → AES-GCM decrypt.</item>
///   <item>Pick out <c>steamLoginSecure</c> + <c>sessionid</c> (preferring the
///   <c>steamcommunity.com</c>-origin entries when multiple hosts have the same cookie name).</item>
/// </list>
///
/// All operations are read-only against Steam's files. The temp copy of the Cookies DB
/// is deleted in a <c>finally</c> block. If the user logs out of Steam, the next read
/// returns <see cref="SteamCefCookieOutcome.MissingRequiredCookies"/> because Steam
/// scrubs the cookies; the caller should ask them to re-log.
/// </summary>
public sealed class SteamCefCookieReader
{
    /// <summary>Default htmlcache path under %LOCALAPPDATA% — Steam writes here for every user.</summary>
    public static string DefaultHtmlCachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Steam", "htmlcache");

    private readonly string _htmlCachePath;

    public SteamCefCookieReader(string? htmlCachePathOverride = null)
    {
        _htmlCachePath = htmlCachePathOverride ?? DefaultHtmlCachePath;
    }

    /// <summary>
    /// Read the live Steam session cookies. Returns a structured result rather than
    /// throwing so the UI layer can render a friendly error per outcome (e.g. "Steam
    /// not installed" vs "you logged out of Steam, please log back in").
    /// </summary>
    public SteamCefCookieReadResult Read()
    {
        var cookiesDbPath = Path.Combine(_htmlCachePath, "Default", "Network", "Cookies");
        var localStatePath = Path.Combine(_htmlCachePath, "Local State");

        if (!File.Exists(cookiesDbPath) || !File.Exists(localStatePath))
        {
            return new SteamCefCookieReadResult(
                SteamCefCookieOutcome.SteamNotInstalled, null,
                $"Steam htmlcache files missing under {_htmlCachePath}");
        }

        byte[] masterKey;
        try { masterKey = LoadMasterKey(localStatePath); }
        catch (Exception ex)
        {
            return new SteamCefCookieReadResult(
                SteamCefCookieOutcome.MasterKeyDecryptFailed, null,
                $"DPAPI-unwrap of master key failed: {ex.Message}. (Are you logged in as the Windows user that owns the Steam install?)");
        }

        string? steamLoginSecure = null;
        string? sessionId = null;

        var tempDb = Path.Combine(Path.GetTempPath(), $"ws-sentinel-cookies-{Guid.NewGuid():N}.db");
        try
        {
            // Copy first to avoid lock contention; CEF holds the live DB open while Steam runs.
            File.Copy(cookiesDbPath, tempDb, overwrite: true);

            using var conn = new SqliteConnection($"Data Source={tempDb};Mode=ReadOnly;Cache=Shared");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT host_key, name, encrypted_value FROM cookies " +
                "WHERE host_key LIKE '%steamcommunity.com%' OR host_key LIKE '%steampowered.com%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var hostKey = reader.GetString(0);
                var name    = reader.GetString(1);
                var blob    = (byte[])reader[2];

                var value = DecryptCookieBlob(blob, masterKey);
                if (value is null) continue;

                if (name == "steamLoginSecure" &&
                    (steamLoginSecure is null || hostKey.Contains("steamcommunity", StringComparison.OrdinalIgnoreCase)))
                {
                    steamLoginSecure = value;
                }
                else if (name == "sessionid" &&
                    (sessionId is null || hostKey.Contains("steamcommunity", StringComparison.OrdinalIgnoreCase)))
                {
                    sessionId = value;
                }
            }
        }
        catch (Exception ex)
        {
            return new SteamCefCookieReadResult(
                SteamCefCookieOutcome.SqliteOpenFailed, null,
                $"Cookie DB read failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempDb)) File.Delete(tempDb); } catch { /* best-effort */ }
        }

        if (string.IsNullOrEmpty(steamLoginSecure) || string.IsNullOrEmpty(sessionId))
        {
            return new SteamCefCookieReadResult(
                SteamCefCookieOutcome.MissingRequiredCookies, null,
                "steamLoginSecure or sessionid not present in Steam's cookie store. " +
                "Open Steam, click any Workshop page once in the overlay/library browser, then retry. " +
                "(Steam writes the cookies on the first authenticated community-page visit per session.)");
        }

        return new SteamCefCookieReadResult(
            SteamCefCookieOutcome.Ok,
            new SteamSessionCookies(steamLoginSecure, sessionId),
            null);
    }

    /// <summary>
    /// Read + DPAPI-unwrap the AES-256 master key from Chromium's <c>Local State</c>.
    /// Format: base64("DPAPI" + dpapi_blob). The DPAPI scope is CurrentUser because Chromium
    /// (and CEF) protects the key against the user's own Windows logon.
    /// </summary>
    public static byte[] LoadMasterKey(string localStatePath)
    {
        var json = File.ReadAllText(localStatePath);
        using var doc = JsonDocument.Parse(json);
        var encB64 = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()
                     ?? throw new InvalidDataException("Local State has no os_crypt.encrypted_key");
        var encBytes = Convert.FromBase64String(encB64);
        if (encBytes.Length < 6 ||
            Encoding.ASCII.GetString(encBytes, 0, 5) != "DPAPI")
        {
            throw new InvalidDataException("Unexpected encrypted_key prefix; expected 'DPAPI'.");
        }
        var dpapiBlob = encBytes[5..];
        return ProtectedData.Unprotect(dpapiBlob, optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Decrypt one Chromium-format cookie value. v10/v11 format is:
    /// 3-byte ASCII prefix "v10" or "v11" + 12-byte IV + ciphertext + 16-byte GCM tag.
    /// Legacy (pre-v10) cookies are DPAPI-wrapped directly — we handle that as a fallback.
    /// Returns null if the blob is malformed or decryption fails (the cookie is unusable;
    /// caller skips it rather than crashing the whole read).
    /// </summary>
    public static string? DecryptCookieBlob(byte[] blob, byte[] masterKey)
    {
        if (blob is null || blob.Length < 3) return null;
        var prefix = Encoding.ASCII.GetString(blob, 0, 3);
        if (prefix != "v10" && prefix != "v11")
        {
            // Legacy DPAPI direct.
            try
            {
                var plain = ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch { return null; }
        }

        // v10/v11: 3-byte prefix + 12-byte IV + ciphertext + 16-byte GCM tag.
        const int prefixLen = 3, ivLen = 12, tagLen = 16;
        if (blob.Length < prefixLen + ivLen + tagLen) return null;
        var iv = new byte[ivLen];
        Array.Copy(blob, prefixLen, iv, 0, ivLen);
        var tag = new byte[tagLen];
        Array.Copy(blob, blob.Length - tagLen, tag, 0, tagLen);
        var cipherLen = blob.Length - prefixLen - ivLen - tagLen;
        var cipher = new byte[cipherLen];
        Array.Copy(blob, prefixLen + ivLen, cipher, 0, cipherLen);

        try
        {
            var plain = new byte[cipherLen];
            using var aes = new AesGcm(masterKey, tagLen);
            aes.Decrypt(iv, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
