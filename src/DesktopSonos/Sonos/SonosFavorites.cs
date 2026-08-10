using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>One entry from the household's Sonos favourites.</summary>
public sealed class SonosFavorite
{
    public string Title { get; init; } = "";

    /// <summary>What the Sonos app calls it — the album, the artist, or the station.</summary>
    public string Description { get; init; } = "";

    /// <summary>The URI to enqueue or, for a station, to set as the transport URI.</summary>
    public string Uri { get; init; } = "";

    /// <summary>
    /// The favourite's own nested DIDL, taken verbatim. It already carries the service token for
    /// whichever account the household is linked to, so nothing has to be rebuilt or guessed.
    /// </summary>
    public string Metadata { get; init; } = "";

    /// <summary>
    /// A radio station or other live stream. These cannot go in the queue at all — the player
    /// takes them as a transport URI instead — so the distinction has to survive to the caller.
    /// </summary>
    public bool IsStream { get; init; }

    /// <summary>"Spotify", "YouTube Music", "Radio" — whatever the household linked it from.</summary>
    public string Service { get; init; } = "";
}

/// <summary>
/// Everything favourited in the Sonos app, read straight off a player. This is the one route to
/// a household's music-service content that needs no account of our own: the favourite carries
/// the service's own URI and token, so playing one is the same call the Sonos app makes.
/// </summary>
public static class SonosFavorites
{
    /// <summary>Where Sonos keeps favourites. FV:2 is the list; FV: is only its parent.</summary>
    private const string ObjectId = "FV:2";

    public static async Task<List<SonosFavorite>> ListAsync(SonosDevice device,
        CancellationToken ct = default)
    {
        var favourites = new List<SonosFavorite>();
        const int pageSize = 100;

        while (true)
        {
            var (didl, returned, total) =
                await device.BrowseAsync(ObjectId, favourites.Count, pageSize, ct).ConfigureAwait(false);

            var page = Parse(didl);
            if (page.Count == 0) break;

            favourites.AddRange(page);
            if (returned <= 0 || favourites.Count >= total) break;
        }

        return favourites;
    }

    /// <summary>
    /// Fills in which service each favourite came from. Separate from parsing because naming
    /// costs a second SOAP call, and a caller that already has the map should not pay it twice.
    /// </summary>
    public static List<SonosFavorite> WithServiceNames(List<SonosFavorite> favourites,
        IReadOnlyDictionary<int, string> services) =>
        favourites
            .Select(f => new SonosFavorite
            {
                Title = f.Title,
                Description = f.Description,
                Uri = f.Uri,
                Metadata = f.Metadata,
                IsStream = f.IsStream,
                Service = SonosMusicServices.NameFor(f.Uri, services)
            })
            .ToList();

    public static List<SonosFavorite> Parse(string didl)
    {
        var favourites = new List<SonosFavorite>();
        if (string.IsNullOrWhiteSpace(didl)) return favourites;

        XDocument doc;
        try { doc = XDocument.Parse(didl); }
        catch (System.Xml.XmlException) { return favourites; }

        // Only the outer items: a favourite's resMD nests a second DIDL with its own <item>,
        // and descending blindly would list every favourite twice.
        var root = doc.Root;
        if (root is null) return favourites;

        foreach (var item in root.Elements().Where(e => e.Name.LocalName == "item"))
        {
            var res = item.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
            var uri = res?.Value ?? "";

            // Some favourites are shortcuts (r:type="shortcut") — the Sonos Radio tiles, for
            // instance. They carry an empty <res> because they open a browse container in the
            // Sonos app rather than playing anything, so there is nothing to queue and listing
            // them would only offer the user a row that cannot do anything.
            if (string.IsNullOrWhiteSpace(uri)) continue;

            var metadata = item.Elements().FirstOrDefault(e => e.Name.LocalName == "resMD")?.Value ?? "";

            favourites.Add(new SonosFavorite
            {
                Title = item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "",
                Description =
                    item.Elements().FirstOrDefault(e => e.Name.LocalName == "description")?.Value ?? "",
                Uri = uri,
                Metadata = metadata,
                IsStream = LooksLikeStream(uri, metadata)
            });
        }

        return favourites;
    }

    /// <summary>
    /// Stations announce themselves two ways and neither is reliable alone: the URI scheme names
    /// a stream, and the metadata's class is audioBroadcast. Either is taken as enough.
    /// </summary>
    private static bool LooksLikeStream(string uri, string metadata) =>
        uri.StartsWith("x-sonosapi-stream:", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("x-sonosapi-radio:", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("x-rincon-mp3radio:", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("x-sonosapi-hls:", StringComparison.OrdinalIgnoreCase) ||
        metadata.Contains("audioBroadcast", StringComparison.OrdinalIgnoreCase);
}
