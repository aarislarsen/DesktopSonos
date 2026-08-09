using System.Text;

namespace DesktopSonos.Sonos;

/// <summary>
/// DIDL-Lite metadata. Sonos will play a bare URI without it, but the display stays blank
/// and some firmware refuses to enqueue, so we always send a well-formed item.
/// </summary>
public static class Didl
{
    private const string Header =
        "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        "xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" " +
        "xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">";

    /// <summary>Tells the player the content comes from a generic UPnP source.</summary>
    private const string Cdudn =
        "<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">" +
        "RINCON_AssociatedZPUDN</desc>";

    /// <summary>
    /// Metadata for one file. Pass <paramref name="uri"/> whenever it is known: without a matching
    /// &lt;res&gt; element the player keeps the URI but discards the rest of the metadata and falls
    /// back to showing the file name, with no artist and no running time. That is what re-queuing
    /// (shuffle, undo) used to lose.
    /// </summary>
    public static string ForTrack(string title, string? artist, string? album,
        string? uri = null, TimeSpan? duration = null)
    {
        var sb = new StringBuilder();
        sb.Append(Header);
        sb.Append("<item id=\"-1\" parentID=\"-1\" restricted=\"true\">");

        if (!string.IsNullOrWhiteSpace(uri))
        {
            var length = duration is { } d && d > TimeSpan.Zero
                ? $" duration=\"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}\""
                : "";
            sb.Append($"<res protocolInfo=\"http-get:*:{MimeFor(uri)}:*\"{length}>{Xml.Escape(uri)}</res>");
        }

        sb.Append($"<dc:title>{Xml.Escape(title)}</dc:title>");
        if (!string.IsNullOrWhiteSpace(artist))
            sb.Append($"<dc:creator>{Xml.Escape(artist)}</dc:creator>");
        if (!string.IsNullOrWhiteSpace(album))
            sb.Append($"<upnp:album>{Xml.Escape(album)}</upnp:album>");
        sb.Append("<upnp:class>object.item.audioItem.musicTrack</upnp:class>");
        sb.Append(Cdudn);
        sb.Append("</item></DIDL-Lite>");
        return sb.ToString();
    }

    /// <summary>
    /// protocolInfo has to name the content type, and the players are stricter about it than the
    /// HTTP layer is. The URLs the media server hands out keep the file's extension for exactly
    /// this reason.
    /// </summary>
    private static string MimeFor(string uri)
    {
        var path = uri;
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];

        var dot = path.LastIndexOf('.');
        var extension = dot >= 0 ? path[(dot + 1)..].ToLowerInvariant() : "";

        return extension switch
        {
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "m4a" or "mp4" or "aac" => "audio/mp4",
            "ogg" or "oga" => "audio/ogg",
            "wma" => "audio/x-ms-wma",
            "wav" => "audio/wav",
            "aif" or "aiff" => "audio/aiff",
            _ => "*"
        };
    }

    /// <summary>Metadata for a continuous stream (our desktop audio feed).</summary>
    public static string ForStream(string title)
    {
        var sb = new StringBuilder();
        sb.Append(Header);
        sb.Append("<item id=\"-1\" parentID=\"-1\" restricted=\"true\">");
        sb.Append($"<dc:title>{Xml.Escape(title)}</dc:title>");
        sb.Append("<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>");
        sb.Append(Cdudn);
        sb.Append("</item></DIDL-Lite>");
        return sb.ToString();
    }

    /// <summary>Best-effort title extraction from DIDL returned by GetPositionInfo.</summary>
    public static string? TitleFrom(string? didl)
    {
        if (string.IsNullOrWhiteSpace(didl)) return null;
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(didl);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
        }
        catch
        {
            return null;
        }
    }
}
