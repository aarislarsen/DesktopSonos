using System.Text;
using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>One entry from the household's saved queues ("Sonos playlists").</summary>
public sealed class SonosPlaylist
{
    /// <summary>"SQ:3" — what DestroyObject and Browse want.</summary>
    public string ObjectId { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>
    /// "file:///jffs/settings/savedqueues.rsq#3" — the URI that has to be enqueued to play it.
    /// It is taken from the player rather than built, because the number after the # is the
    /// playlist's slot on disk and does not have to match the object id.
    /// </summary>
    public string Uri { get; init; } = "";
}

/// <summary>
/// Saved queues live on the players, not in any cloud account, so a playlist made here can mix
/// Spotify tracks with files served from this PC — which is the whole reason for using them
/// rather than Spotify's own playlists.
/// </summary>
public static class SonosPlaylists
{
    private const string DidlHeader =
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" " +
        "xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">";

    public static async Task<List<SonosPlaylist>> ListAsync(SonosDevice device,
        CancellationToken ct = default)
    {
        var playlists = new List<SonosPlaylist>();
        const int pageSize = 100;

        while (true)
        {
            var (didl, returned, total) =
                await device.BrowseAsync("SQ:", playlists.Count, pageSize, ct).ConfigureAwait(false);

            var page = Parse(didl);
            if (page.Count == 0) break;

            playlists.AddRange(page);
            if (returned <= 0 || playlists.Count >= total) break;
        }

        return playlists;
    }

    public static List<SonosPlaylist> Parse(string didl)
    {
        var playlists = new List<SonosPlaylist>();
        if (string.IsNullOrWhiteSpace(didl)) return playlists;

        XDocument doc;
        try { doc = XDocument.Parse(didl); }
        catch (System.Xml.XmlException) { return playlists; }

        foreach (var container in doc.Descendants().Where(e => e.Name.LocalName == "container"))
        {
            var id = (string?)container.Attribute("id") ?? "";
            if (!id.StartsWith("SQ:", StringComparison.Ordinal)) continue;

            playlists.Add(new SonosPlaylist
            {
                ObjectId = id,
                Title = container.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? id,
                Uri = container.Elements().FirstOrDefault(e => e.Name.LocalName == "res")?.Value ?? ""
            });
        }

        return playlists;
    }

    /// <summary>One track inside a saved queue, kept with the metadata needed to re-enqueue it.</summary>
    public sealed class Entry
    {
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Album { get; init; } = "";
        public TimeSpan Duration { get; init; }
        public string Uri { get; init; } = "";

        /// <summary>
        /// The entry wrapped back into a DIDL-Lite document. Passed through verbatim so a
        /// YouTube Music or Spotify track keeps the service token the player gave it — nothing
        /// about the entry has to be understood to put it back in a queue.
        /// </summary>
        public string Metadata { get; init; } = "";
    }

    /// <summary>
    /// Reads what is inside a saved queue. This is how content from services that cannot be
    /// searched — YouTube Music above all, whose ids are opaque service tokens — is still
    /// reachable one track at a time.
    /// </summary>
    public static async Task<List<Entry>> ReadEntriesAsync(SonosDevice device, string objectId,
        int maxEntries = 500, CancellationToken ct = default)
    {
        var entries = new List<Entry>();
        const int pageSize = 100;

        while (entries.Count < maxEntries)
        {
            var (didl, returned, total) =
                await device.BrowseAsync(objectId, entries.Count, pageSize, ct).ConfigureAwait(false);

            var page = ParseEntries(didl);
            if (page.Count == 0) break;

            entries.AddRange(page);
            if (returned <= 0 || entries.Count >= total) break;
        }

        return entries;
    }

    public static List<Entry> ParseEntries(string didl)
    {
        var entries = new List<Entry>();
        if (string.IsNullOrWhiteSpace(didl)) return entries;

        XDocument doc;
        try { doc = XDocument.Parse(didl); }
        catch (System.Xml.XmlException) { return entries; }

        var root = doc.Root;
        if (root is null) return entries;

        foreach (var item in root.Elements().Where(e => e.Name.LocalName == "item"))
        {
            string Child(string local) =>
                item.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value ?? "";

            var res = item.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
            var uri = res?.Value ?? "";
            if (string.IsNullOrWhiteSpace(uri)) continue;

            TimeSpan.TryParse((string?)res?.Attribute("duration"), out var duration);

            // Re-wrapped rather than rebuilt: the item element already holds whatever the service
            // needs, including its cdudn token, and none of it has to be interpreted here.
            var wrapper = new XElement(root.Name, item);
            foreach (var attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration))
                if (wrapper.Attribute(attribute.Name) is null) wrapper.Add(attribute);

            entries.Add(new Entry
            {
                Title = Child("title"),
                Artist = Child("creator"),
                Album = Child("album"),
                Duration = duration,
                Uri = uri,
                Metadata = wrapper.ToString(SaveOptions.DisableFormatting)
            });
        }

        return entries;
    }

    /// <summary>
    /// Metadata for enqueuing a saved queue. The cdudn is the generic one, not a service token:
    /// the entries inside carry their own, so a mixed playlist keeps working.
    /// </summary>
    public static string Metadata(SonosPlaylist playlist)
    {
        var sb = new StringBuilder();
        sb.Append(DidlHeader);
        sb.Append($"<item id=\"{Xml.Escape(playlist.ObjectId)}\" parentID=\"SQ:\" restricted=\"true\">");
        sb.Append($"<dc:title>{Xml.Escape(playlist.Title)}</dc:title>");
        sb.Append("<upnp:class>object.container.playlistContainer</upnp:class>");
        sb.Append("<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">" +
                  "RINCON_AssociatedZPUDN</desc>");
        sb.Append("</item></DIDL-Lite>");
        return sb.ToString();
    }
}
