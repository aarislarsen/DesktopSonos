using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>
/// Maps a Sonos service id to the name people know it by, so a favourite can say "YouTube Music"
/// rather than "sid 284". The list is read from a player rather than hard-coded: the ids are
/// assigned by Sonos, a household can carry services this app has never heard of, and guessing
/// would be wrong the moment one changed.
/// </summary>
public static class SonosMusicServices
{
    private const string Service = "urn:schemas-upnp-org:service:MusicServices:1";
    private const string Control = "/MusicServices/Control";

    /// <summary>
    /// Schemes that identify content by what it is rather than by which service it came from.
    /// These never carry an sid, so the name has to come from the scheme itself.
    /// </summary>
    private static readonly (string Prefix, string Name)[] SchemeNames =
    {
        ("x-file-cifs:", "Library"),
        ("x-rincon-mp3radio:", "Radio"),
        ("x-sonosapi-stream:", "Radio"),
        ("x-sonosapi-radio:", "Radio"),
        ("x-rincon-stream:", "Line-in"),
        ("x-sonos-htastream:", "TV"),
        ("http:", "Library"),
        ("https:", "Library")
    };

    private static readonly Regex SidPattern = new(@"sid(?:=|%3d)(\d+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// The whole catalogue Sonos knows about, which is far larger than what a household has
    /// linked — around 200 entries. That is fine: it is one call, cached by the caller, and it
    /// means any service that turns up in a favourite can be named.
    /// </summary>
    public static async Task<Dictionary<int, string>> ListAsync(SonosDevice device,
        CancellationToken ct = default)
    {
        var names = new Dictionary<int, string>();

        try
        {
            var response = await SonosSoap.InvokeAsync(device.Host, Control, Service,
                "ListAvailableServices", null, ct).ConfigureAwait(false);

            var list = response.GetValueOrDefault("AvailableServiceDescriptorList");
            if (string.IsNullOrWhiteSpace(list)) return names;

            var doc = XDocument.Parse(list);
            foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "Service"))
            {
                if (!int.TryParse((string?)element.Attribute("Id"), out var id)) continue;
                var name = (string?)element.Attribute("Name");
                if (!string.IsNullOrWhiteSpace(name)) names[id] = name;
            }
        }
        catch
        {
            // Naming services is decoration; failing to do it must not stop favourites listing.
        }

        return names;
    }

    /// <summary>The sid carried in a content URI, if it has one.</summary>
    public static int? SidFrom(string uri)
    {
        var match = SidPattern.Match(uri);
        return match.Success && int.TryParse(match.Groups[1].Value, out var sid) ? sid : null;
    }

    /// <summary>
    /// Names whatever a URI points at. Falls back to the scheme for non-service content, and to
    /// the bare sid when the household knows a service this player's catalogue does not name.
    /// </summary>
    public static string NameFor(string uri, IReadOnlyDictionary<int, string> services)
    {
        if (string.IsNullOrWhiteSpace(uri)) return "";

        if (SidFrom(uri) is { } sid)
            return services.TryGetValue(sid, out var name) ? name : $"Service {sid}";

        foreach (var (prefix, name) in SchemeNames)
            if (uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name;

        return "";
    }
}
