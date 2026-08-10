using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopSonos.Persistence;

/// <summary>
/// The Spotify link, kept apart from settings.json because it holds a credential. The refresh
/// token is a long-lived key to the account, so it is encrypted with DPAPI under the current
/// Windows user: copying spotify.json to another machine or another account yields nothing.
/// </summary>
public sealed class SpotifyStore
{
    /// <summary>Ciphertext, base64. Never the token itself.</summary>
    public string ProtectedRefreshToken { get; set; } = "";

    /// <summary>Shown in the settings drawer so it is obvious which account is linked.</summary>
    public string DisplayName { get; set; } = "";

    public string UserId { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Ties the ciphertext to this app, so another program's DPAPI blob cannot be swapped in.</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DesktopSonos.Spotify.v1");

    public static string FilePath => Path.Combine(AppSettings.DirectoryPath, "spotify.json");

    public static SpotifyStore Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new SpotifyStore();
            return JsonSerializer.Deserialize<SpotifyStore>(File.ReadAllText(FilePath), Options)
                   ?? new SpotifyStore();
        }
        catch
        {
            return new SpotifyStore();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Losing the link only costs one more sign-in; it is not worth interrupting playback.
        }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* ignore */ }
    }

    public string? ReadRefreshToken()
    {
        if (string.IsNullOrEmpty(ProtectedRefreshToken)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedRefreshToken),
                Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Written by another Windows account, or the profile was rebuilt. Signing in again
            // is the only way back, and pretending there is no token gets us there.
            return null;
        }
    }

    public void WriteRefreshToken(string token)
    {
        ProtectedRefreshToken = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
    }
}
