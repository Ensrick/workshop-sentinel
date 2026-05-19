using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using WorkshopSentinel.Services;
using Xunit;

namespace WorkshopSentinel.Tests;

public class SteamCefCookieReaderTests
{
    [Fact]
    public void DecryptCookieBlob_round_trips_a_v10_payload()
    {
        // Build a fake Chromium v10 blob with a known AES-256 key. This proves the
        // unwrap (prefix + IV + ciphertext + tag layout) is correct end-to-end without
        // needing DPAPI or a real Steam install.
        var key = RandomNumberGenerator.GetBytes(32);
        var iv  = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes("76561198211120891%7Ctoken-blob-here-deadbeef");
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, tag.Length))
            aes.Encrypt(iv, plaintext, cipher, tag);

        var blob = Concat(Encoding.ASCII.GetBytes("v10"), iv, cipher, tag);
        var decoded = SteamCefCookieReader.DecryptCookieBlob(blob, key);
        Assert.Equal("76561198211120891%7Ctoken-blob-here-deadbeef", decoded);
    }

    [Fact]
    public void DecryptCookieBlob_returns_null_when_blob_is_truncated()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        // "v10" + 4 bytes — well below the 31-byte minimum (3 + 12 + 0 + 16).
        var blob = Concat(Encoding.ASCII.GetBytes("v10"), new byte[] { 1, 2, 3, 4 });
        Assert.Null(SteamCefCookieReader.DecryptCookieBlob(blob, key));
    }

    [Fact]
    public void DecryptCookieBlob_returns_null_on_tag_mismatch()
    {
        // A v10 blob whose GCM tag is junk — AES-GCM must reject it without crashing.
        var key = RandomNumberGenerator.GetBytes(32);
        var iv  = new byte[12];
        var cipher = new byte[10];
        var badTag = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var blob = Concat(Encoding.ASCII.GetBytes("v10"), iv, cipher, badTag);
        Assert.Null(SteamCefCookieReader.DecryptCookieBlob(blob, key));
    }

    [Fact]
    public void DecryptCookieBlob_returns_null_for_empty_input()
    {
        Assert.Null(SteamCefCookieReader.DecryptCookieBlob(Array.Empty<byte>(), new byte[32]));
        Assert.Null(SteamCefCookieReader.DecryptCookieBlob(null!, new byte[32]));
    }

    [Fact]
    public void LoadMasterKey_throws_on_missing_field()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-sentinel-localstate-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, "{\"os_crypt\":{}}");
        try
        {
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() =>
                SteamCefCookieReader.LoadMasterKey(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void LoadMasterKey_rejects_bad_prefix()
    {
        // Build a Local State that has an encrypted_key but its first 5 bytes aren't "DPAPI".
        var badBlob = Encoding.ASCII.GetBytes("XXXXX" + new string((char)0, 32));
        var json = "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String(badBlob) + "\"}}";
        var tmp = Path.Combine(Path.GetTempPath(), $"ws-sentinel-localstate-{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, json);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                SteamCefCookieReader.LoadMasterKey(tmp));
            Assert.Contains("DPAPI", ex.Message);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Read_returns_SteamNotInstalled_when_htmlcache_missing()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), $"ws-sentinel-nohtmlcache-{Guid.NewGuid():N}");
        var reader = new SteamCefCookieReader(nonexistent);
        var result = reader.Read();
        Assert.Equal(SteamCefCookieOutcome.SteamNotInstalled, result.Outcome);
        Assert.Null(result.Cookies);
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays) total += a.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            Array.Copy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }
}
