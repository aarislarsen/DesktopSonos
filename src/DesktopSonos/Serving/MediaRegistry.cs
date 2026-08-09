using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DesktopSonos.Serving;

/// <summary>
/// Maps opaque tokens to local/UNC file paths so the HTTP server never accepts a
/// caller-supplied path (no directory traversal, nothing outside the library is reachable).
/// </summary>
public sealed class MediaRegistry
{
    private readonly ConcurrentDictionary<string, string> _idToPath = new(StringComparer.Ordinal);

    /// <summary>
    /// Tokens are derived from the path rather than random, so a URL handed to a speaker in one
    /// session still resolves in the next. Sonos keeps the queue on the player across restarts
    /// of this app, and a random token would leave every one of those entries pointing at a 404.
    /// </summary>
    public static string TokenFor(string fullPath)
    {
        var normalized = fullPath.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public string Register(string fullPath)
    {
        var id = TokenFor(fullPath);
        _idToPath[id] = fullPath;
        return id;
    }

    /// <summary>Re-registers a whole library so previously issued URLs start resolving again.</summary>
    public void RegisterAll(IEnumerable<string> paths)
    {
        foreach (var path in paths) Register(path);
    }

    public bool TryResolve(string id, out string fullPath)
    {
        if (_idToPath.TryGetValue(id, out var value))
        {
            fullPath = value;
            return true;
        }
        fullPath = string.Empty;
        return false;
    }

    public void Clear() => _idToPath.Clear();

    public int Count => _idToPath.Count;
}

public static class MimeTypes
{
    public static string ForFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp3" => "audio/mpeg",
            ".m4a" or ".m4b" or ".mp4" or ".aac" => "audio/mp4",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".wma" => "audio/x-ms-wma",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".aif" or ".aiff" => "audio/aiff",
            ".alac" => "audio/mp4",
            _ => "application/octet-stream"
        };
    }
}
