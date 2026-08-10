using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>
/// Which Spotify account the household is linked to. Sonos needs all three numbers on every
/// Spotify URI it is handed:
/// <list type="bullet">
/// <item><c>sid</c> — the music service id. Spotify is 9 on every household seen so far, but it
/// is not guaranteed, so it is read from the player rather than hard-coded.</item>
/// <item><c>sn</c> — *which* linked account. A household with two Spotify logins has sn=1 and
/// sn=2, and the wrong one plays nothing.</item>
/// <item><c>cdudn</c> — the token string in the DIDL <c>&lt;desc&gt;</c> element. Without it the
/// player treats the item as generic UPnP content and refuses it.</item>
/// </list>
/// </summary>
public sealed record SpotifyAccount(int Sid, int Sn, string Cdudn)
{
    /// <summary>
    /// What Sonos has used for Spotify for years. Only reached when nothing on the household
    /// references Spotify yet, which is also the case where playback is least likely to work —
    /// so it is a last resort, not a default worth relying on.
    /// </summary>
    public static SpotifyAccount Fallback { get; } = FromSid(9, 1);

    /// <summary>
    /// The cdudn number is <c>sid * 256 + accountType</c>, and Spotify's OAuth account type is 7:
    /// sid 9 gives 2311, which is exactly what this household's favourites carry. Deriving it
    /// means a household on a different sid still gets a usable token.
    /// </summary>
    public static SpotifyAccount FromSid(int sid, int sn)
    {
        var token = sid * 256 + 7;
        return new SpotifyAccount(sid, sn, $"SA_RINCON{token}_X_#Svc{token}-0-Token");
    }
}

/// <summary>
/// Turns Spotify ids into the URIs and metadata a Sonos player accepts, so Spotify content goes
/// into the ordinary player queue beside the files served from this PC. Nothing here streams
/// audio: the player fetches it from Spotify itself, which is why the household has to have
/// Spotify linked in the Sonos app first.
/// </summary>
public static class SonosSpotify
{
    /// <summary>Object-id prefixes Sonos uses to tell one kind of Spotify content from another.</summary>
    private const string TrackPrefix = "10032020";
    private const string AlbumPrefix = "1004206c";
    private const string PlaylistPrefix = "1006206c";
    private const string ArtistPrefix = "10052064";

    /// <summary>
    /// Flag words copied from what the Sonos app itself writes into favourites: 8224 marks a
    /// single playable track, 8300 a container the player has to expand into queue entries.
    /// </summary>
    private const int TrackFlags = 8224;
    private const int ContainerFlags = 8300;

    private const string DidlHeader =
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" " +
        "xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">";

    // ---------------------------------------------------------------- URIs

    /// <summary>
    /// Sonos wants the Spotify URI percent-encoded *inside* its own URI, with lowercase escapes —
    /// `spotify:track:x` becomes `spotify%3atrack%3ax`. Uppercase %3A is rejected by some
    /// firmware, so this does not use Uri.EscapeDataString.
    /// </summary>
    private static string Encode(string spotifyUri) => spotifyUri.Replace(":", "%3a");

    public static string TrackUri(SpotifyAccount account, string trackId) =>
        $"x-sonos-spotify:{Encode($"spotify:track:{trackId}")}" +
        $"?sid={account.Sid}&flags={TrackFlags}&sn={account.Sn}";

    public static string AlbumUri(SpotifyAccount account, string albumId) =>
        ContainerUri(account, AlbumPrefix, $"spotify:album:{albumId}");

    public static string PlaylistUri(SpotifyAccount account, string playlistId) =>
        ContainerUri(account, PlaylistPrefix, $"spotify:playlist:{playlistId}");

    public static string ArtistUri(SpotifyAccount account, string artistId) =>
        ContainerUri(account, ArtistPrefix, $"spotify:artist:{artistId}");

    private static string ContainerUri(SpotifyAccount account, string prefix, string spotifyUri) =>
        $"x-rincon-cpcontainer:{prefix}{Encode(spotifyUri)}" +
        $"?sid={account.Sid}&flags={ContainerFlags}&sn={account.Sn}";

    // ---------------------------------------------------------------- metadata

    /// <summary>
    /// Metadata for one Spotify track. Deliberately carries no &lt;res&gt;: the enqueued URI is
    /// passed separately, and a res element pointing at a service URI makes some firmware try to
    /// fetch it over plain HTTP and fail.
    /// </summary>
    public static string TrackMetadata(SpotifyAccount account, string trackId, string title,
        string? artist, string? album) =>
        Item(account, TrackPrefix + Encode($"spotify:track:{trackId}"),
            "object.item.audioItem.musicTrack", title, artist, album);

    public static string AlbumMetadata(SpotifyAccount account, string albumId, string title,
        string? artist) =>
        Item(account, AlbumPrefix + Encode($"spotify:album:{albumId}"),
            "object.container.album.musicAlbum", title, artist, null);

    public static string PlaylistMetadata(SpotifyAccount account, string playlistId, string title) =>
        Item(account, PlaylistPrefix + Encode($"spotify:playlist:{playlistId}"),
            "object.container.playlistContainer", title, null, null);

    public static string ArtistMetadata(SpotifyAccount account, string artistId, string title) =>
        Item(account, ArtistPrefix + Encode($"spotify:artist:{artistId}"),
            "object.container.person.musicArtist", title, null, null);

    private static string Item(SpotifyAccount account, string id, string upnpClass, string title,
        string? artist, string? album)
    {
        var sb = new StringBuilder();
        sb.Append(DidlHeader);
        sb.Append($"<item id=\"{Xml.Escape(id)}\" parentID=\"-1\" restricted=\"true\">");
        sb.Append($"<dc:title>{Xml.Escape(title)}</dc:title>");
        if (!string.IsNullOrWhiteSpace(artist))
            sb.Append($"<dc:creator>{Xml.Escape(artist)}</dc:creator>");
        if (!string.IsNullOrWhiteSpace(album))
            sb.Append($"<upnp:album>{Xml.Escape(album)}</upnp:album>");
        sb.Append($"<upnp:class>{upnpClass}</upnp:class>");
        sb.Append("<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">");
        sb.Append(Xml.Escape(account.Cdudn));
        sb.Append("</desc></item></DIDL-Lite>");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- discovery

    /// <summary>Matches sid/sn in a plain URI and in the doubly-escaped form inside albumArtURI.</summary>
    private static readonly Regex SidPattern = new(@"sid(?:=|%3d)(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex SnPattern = new(@"sn(?:=|%3d)(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex CdudnPattern = new(@"SA_RINCON\d+_X_#Svc\d+-\d+-Token");

    /// <summary>
    /// Works out which Spotify account the household is linked to by reading back something the
    /// Sonos app already wrote. There is no API that simply reports it — <c>/status/accounts</c>
    /// returns an empty document on current firmware — so this looks at content instead:
    /// favourites first, then saved queues. Both are cheap Browse calls against the player.
    /// Returns null when the household has no Spotify content at all, which almost always means
    /// Spotify is not linked yet.
    /// </summary>
    public static async Task<SpotifyAccount?> DiscoverAsync(SonosDevice device,
        CancellationToken ct = default)
    {
        var fromFavourites = await FromFavouritesAsync(device, ct).ConfigureAwait(false);
        if (fromFavourites is not null) return fromFavourites;

        return await FromSavedQueuesAsync(device, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Favourites are the best source: a Spotify favourite carries the account's own cdudn token
    /// in its nested <c>r:resMD</c> document, so nothing has to be derived.
    /// </summary>
    private static async Task<SpotifyAccount?> FromFavouritesAsync(SonosDevice device,
        CancellationToken ct)
    {
        try
        {
            var (didl, _, _) = await device.BrowseAsync("FV:2", 0, 100, ct).ConfigureAwait(false);
            return FromDidl(didl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saved queues are the fallback: the container listing only exposes sid and sn (buried in
    /// the album-art URLs), so the cdudn token is derived from the sid.
    /// </summary>
    private static async Task<SpotifyAccount?> FromSavedQueuesAsync(SonosDevice device,
        CancellationToken ct)
    {
        try
        {
            var (didl, _, _) = await device.BrowseAsync("SQ:", 0, 100, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(didl)) return null;

            // The tracks inside a saved queue carry full metadata, so try them before falling
            // back to scraping the art URLs of the containers themselves.
            foreach (var id in SonosPlaylists.Parse(didl).Select(p => p.ObjectId).Take(8))
            {
                var (children, _, _) = await device.BrowseAsync(id, 0, 20, ct).ConfigureAwait(false);
                var found = FromDidl(children);
                if (found is not null) return found;
            }

            return ScrapeSidAndSn(didl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pulls an account out of any DIDL document that mentions Spotify.</summary>
    private static SpotifyAccount? FromDidl(string didl)
    {
        if (string.IsNullOrWhiteSpace(didl)) return null;

        XDocument doc;
        try { doc = XDocument.Parse(didl); }
        catch (System.Xml.XmlException) { return ScrapeSidAndSn(didl); }

        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var res = item.Elements().FirstOrDefault(e => e.Name.LocalName == "res")?.Value;
            if (res is null || res.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) < 0) continue;

            var sid = Capture(SidPattern, res);
            var sn = Capture(SnPattern, res);
            if (sid is null) continue;

            // The token lives in the nested resMD document; take it verbatim when it is there
            // rather than deriving it, since a household could in principle carry another type.
            var cdudn = CdudnPattern.Match(item.ToString()).Value;
            return string.IsNullOrEmpty(cdudn)
                ? SpotifyAccount.FromSid(sid.Value, sn ?? 1)
                : new SpotifyAccount(sid.Value, sn ?? 1, cdudn);
        }

        return ScrapeSidAndSn(didl);
    }

    /// <summary>
    /// Last resort within a document: find any Spotify reference and read the numbers off it,
    /// including the escaped-twice form Sonos uses inside <c>/getaa</c> album-art URLs.
    /// </summary>
    private static SpotifyAccount? ScrapeSidAndSn(string text)
    {
        var marker = text.IndexOf("x-sonos-spotify", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;

        // Stay inside the one reference: a document can mix services, and taking sn from a
        // YouTube Music entry would point at the wrong account.
        var end = text.IndexOf('<', marker);
        var slice = end > marker ? text[marker..end] : text[marker..];

        var sid = Capture(SidPattern, slice);
        if (sid is null) return null;

        return SpotifyAccount.FromSid(sid.Value, Capture(SnPattern, slice) ?? 1);
    }

    private static int? Capture(Regex pattern, string text)
    {
        var match = pattern.Match(text);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }
}
