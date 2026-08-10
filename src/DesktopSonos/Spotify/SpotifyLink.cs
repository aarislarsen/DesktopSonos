using System.Text.RegularExpressions;
using DesktopSonos.Music;

namespace DesktopSonos.Spotify;

/// <summary>
/// Turns whatever Spotify's share button produces into something the players can be handed.
/// This needs no account and no API call at all — the id is right there in the link — so it is
/// the way to play a specific album or playlist without signing in to anything.
/// </summary>
public static class SpotifyLink
{
    /// <summary>
    /// Covers both forms Spotify hands out: "spotify:album:xyz" from Copy Spotify URI, and
    /// "https://open.spotify.com/album/xyz?si=…" from Copy link. The optional locale segment
    /// ("/intl-da/") appears on links shared from the mobile app.
    /// </summary>
    private static readonly Regex Pattern = new(
        @"(?:spotify:|https?://open\.spotify\.com/(?:intl-[a-z-]+/)?)" +
        @"(track|album|playlist|artist)[:/]([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Reads a link. Returns null when the text is not a Spotify link at all. Titles are not
    /// available without an API call, so the item is named after the link until the player
    /// replaces it with the real metadata once it is in the queue.
    /// </summary>
    public static MusicItem? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = Pattern.Match(text.Trim());
        if (!match.Success) return null;

        var kind = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "track" => MusicItemKind.Track,
            "album" => MusicItemKind.Album,
            "playlist" => MusicItemKind.Playlist,
            "artist" => MusicItemKind.Artist,
            _ => (MusicItemKind?)null
        };

        if (kind is null) return null;

        var id = match.Groups[2].Value;
        return new MusicItem
        {
            Service = "Spotify",
            Kind = kind.Value,
            Id = id,
            Title = $"Spotify {match.Groups[1].Value.ToLowerInvariant()}",
            Subtitle = "from a link",
            Detail = id
        };
    }
}
