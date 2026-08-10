using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DesktopSonos.Music;

namespace DesktopSonos.Spotify;

/// <summary>
/// The Spotify Web API, used only to find things and to read the account's own lists. It never
/// carries audio — the players fetch that from Spotify themselves — so nothing here is on the
/// playback path and a slow reply only makes the list arrive late.
/// </summary>
public sealed class SpotifyApi
{
    private const string Base = "https://api.spotify.com/v1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SpotifyAuth _auth;

    public SpotifyApi(SpotifyAuth auth) => _auth = auth;

    public event Action<string>? Log;

    // ---------------------------------------------------------------- account

    /// <summary>Returns (display name, id), or null when nobody is signed in.</summary>
    public async Task<(string Name, string Id)?> GetMeAsync(CancellationToken ct = default)
    {
        var root = await GetAsync("/me", ct).ConfigureAwait(false);
        if (root is null) return null;

        var value = root.Value;
        var name = Text(value, "display_name");
        var id = Text(value, "id");
        return (string.IsNullOrEmpty(name) ? id : name, id);
    }

    // ---------------------------------------------------------------- search

    /// <summary>
    /// One call covering all three kinds, so a search for "abbey road" turns up the album as
    /// well as the tracks. Results are grouped tracks-then-albums-then-playlists because that is
    /// the order they are usually wanted in.
    /// </summary>
    public async Task<List<MusicItem>> SearchAsync(string query, bool tracks, bool albums,
        bool playlists, int limit = 30, CancellationToken ct = default)
    {
        var kinds = new List<string>();
        if (tracks) kinds.Add("track");
        if (albums) kinds.Add("album");
        if (playlists) kinds.Add("playlist");
        if (kinds.Count == 0 || string.IsNullOrWhiteSpace(query)) return new List<MusicItem>();

        var path = $"/search?q={Uri.EscapeDataString(query)}" +
                   $"&type={string.Join(',', kinds)}&limit={Math.Clamp(limit, 1, 50)}";

        var root = await GetAsync(path, ct).ConfigureAwait(false);
        if (root is null) return new List<MusicItem>();

        var results = new List<MusicItem>();
        AppendItems(root.Value, "tracks", TrackFrom, results);
        AppendItems(root.Value, "albums", AlbumFrom, results);
        AppendItems(root.Value, "playlists", PlaylistFrom, results);
        return results;
    }

    private static void AppendItems(JsonElement root, string property,
        Func<JsonElement, MusicItem?> map, List<MusicItem> into)
    {
        if (!root.TryGetProperty(property, out var section)) return;
        if (!section.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var element in items.EnumerateArray())
        {
            // Spotify's search happily returns nulls in the playlist array; mapping one throws.
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (map(element) is { } item) into.Add(item);
        }
    }

    // ---------------------------------------------------------------- the account's own lists

    public Task<List<MusicItem>> GetMyPlaylistsAsync(CancellationToken ct = default) =>
        PageAsync("/me/playlists?limit=50", PlaylistFrom, 500, ct);

    /// <summary>Saved albums arrive wrapped in a { added_at, album } envelope.</summary>
    public Task<List<MusicItem>> GetSavedAlbumsAsync(CancellationToken ct = default) =>
        PageAsync("/me/albums?limit=50", e => Unwrap(e, "album", AlbumFrom), 500, ct);

    public Task<List<MusicItem>> GetLikedTracksAsync(CancellationToken ct = default) =>
        PageAsync("/me/tracks?limit=50", e => Unwrap(e, "track", TrackFrom), 1000, ct);

    public Task<List<MusicItem>> GetPlaylistTracksAsync(string playlistId,
        CancellationToken ct = default) =>
        PageAsync($"/playlists/{Uri.EscapeDataString(playlistId)}/tracks?limit=100",
            e => Unwrap(e, "track", TrackFrom), 1000, ct);

    /// <summary>
    /// Album tracks come back in their simplified form, which omits the album itself, so the
    /// album name is filled in by the caller's context rather than left blank.
    /// </summary>
    public Task<List<MusicItem>> GetAlbumTracksAsync(string albumId, string albumName,
        CancellationToken ct = default) =>
        PageAsync($"/albums/{Uri.EscapeDataString(albumId)}/tracks?limit=50",
            e => TrackFrom(e, albumName), 500, ct);

    /// <summary>An artist's ten most-played tracks — what "open an artist" is useful for.</summary>
    public async Task<List<MusicItem>> GetArtistTopTracksAsync(string artistId,
        CancellationToken ct = default)
    {
        var root = await GetAsync($"/artists/{Uri.EscapeDataString(artistId)}/top-tracks?market=from_token", ct)
            .ConfigureAwait(false);
        if (root is null) return new List<MusicItem>();

        var results = new List<MusicItem>();
        if (root.Value.TryGetProperty("tracks", out var tracks) &&
            tracks.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in tracks.EnumerateArray())
                if (TrackFrom(element) is { } item) results.Add(item);
        }
        return results;
    }

    private static MusicItem? Unwrap(JsonElement envelope, string property,
        Func<JsonElement, MusicItem?> map)
    {
        // A track removed from Spotify, or a local file in a playlist, comes through as null.
        if (!envelope.TryGetProperty(property, out var inner) ||
            inner.ValueKind != JsonValueKind.Object) return null;
        return map(inner);
    }

    // ---------------------------------------------------------------- mapping

    private static MusicItem? TrackFrom(JsonElement track) => TrackFrom(track, null);

    private static MusicItem? TrackFrom(JsonElement track, string? albumFallback)
    {
        var id = Text(track, "id");
        // Local files a user has added to a playlist have no id and cannot be played on Sonos.
        if (string.IsNullOrEmpty(id)) return null;

        var album = albumFallback ?? "";
        if (track.TryGetProperty("album", out var albumElement) &&
            albumElement.ValueKind == JsonValueKind.Object)
        {
            album = Text(albumElement, "name");
        }

        return new MusicItem
        {
            Service = "Spotify",
            Kind = MusicItemKind.Track,
            Id = id,
            Title = Text(track, "name"),
            Subtitle = Artists(track),
            Detail = album,
            Duration = TimeSpan.FromMilliseconds(Number(track, "duration_ms"))
        };
    }

    private static MusicItem? AlbumFrom(JsonElement album)
    {
        var id = Text(album, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var count = (int)Number(album, "total_tracks");
        return new MusicItem
        {
            Service = "Spotify",
            Kind = MusicItemKind.Album,
            Id = id,
            Title = Text(album, "name"),
            Subtitle = Artists(album),
            Detail = count > 0 ? $"{count} tracks" : ""
        };
    }

    private static MusicItem? PlaylistFrom(JsonElement playlist)
    {
        var id = Text(playlist, "id");
        if (string.IsNullOrEmpty(id)) return null;

        var count = 0;
        if (playlist.TryGetProperty("tracks", out var tracks) &&
            tracks.ValueKind == JsonValueKind.Object)
        {
            count = (int)Number(tracks, "total");
        }

        var owner = playlist.TryGetProperty("owner", out var ownerElement) &&
                    ownerElement.ValueKind == JsonValueKind.Object
            ? Text(ownerElement, "display_name")
            : "";

        return new MusicItem
        {
            Service = "Spotify",
            Kind = MusicItemKind.Playlist,
            Id = id,
            Title = Text(playlist, "name"),
            Subtitle = owner,
            Detail = count > 0 ? $"{count} tracks" : ""
        };
    }

    private static string Artists(JsonElement element)
    {
        if (!element.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Array) return "";

        return string.Join(", ", artists.EnumerateArray()
            .Where(a => a.ValueKind == JsonValueKind.Object)
            .Select(a => Text(a, "name"))
            .Where(n => !string.IsNullOrEmpty(n)));
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    // ---------------------------------------------------------------- transport

    /// <summary>
    /// Follows Spotify's "next" links until the list is exhausted or the cap is reached. The cap
    /// exists because a Sonos queue holds 500 entries, so pulling a 12 000-track liked-songs list
    /// in full would be a lot of requests for nothing.
    /// </summary>
    private async Task<List<MusicItem>> PageAsync(string path,
        Func<JsonElement, MusicItem?> map, int cap, CancellationToken ct)
    {
        var results = new List<MusicItem>();
        var next = path;

        while (next is not null && results.Count < cap)
        {
            var root = await GetAsync(next, ct).ConfigureAwait(false);
            if (root is null) break;

            if (root.Value.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in items.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    if (map(element) is { } item) results.Add(item);
                }
            }

            next = root.Value.TryGetProperty("next", out var link) &&
                   link.ValueKind == JsonValueKind.String
                ? link.GetString()
                : null;
        }

        return results;
    }

    /// <summary>
    /// A GET with the bearer token attached. Returns null rather than throwing when nobody is
    /// signed in, so the panel can show its connect prompt instead of an error.
    /// </summary>
    private async Task<JsonElement?> GetAsync(string pathOrUrl, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return null;

        var url = pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? pathOrUrl
            : Base + pathOrUrl;

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            // Spotify rate-limits per app, and the reply names how long to wait. Two attempts is
            // enough for the bursts a person typing in a search box can produce.
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                Log?.Invoke($"Spotify asked us to slow down; waiting {wait.TotalSeconds:0}s.");
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new SpotifyApiException((int)response.StatusCode, Describe(response, body));

            // 204 on an empty result, and JsonDocument will not parse an empty string.
            if (string.IsNullOrWhiteSpace(body)) return null;

            // Clone: the JsonDocument is disposed on the way out of this method, and an
            // undetached JsonElement becomes invalid the moment it is.
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
    }

    private static string Describe(HttpResponseMessage response, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                var message = Text(error, "message");
                if (!string.IsNullOrEmpty(message)) return message;
            }
        }
        catch { /* not JSON */ }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }
}

public sealed class SpotifyApiException : Exception
{
    public SpotifyApiException(int statusCode, string message) : base(message) =>
        StatusCode = statusCode;

    public int StatusCode { get; }
}
