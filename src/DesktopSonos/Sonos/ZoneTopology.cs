using System.Net;
using System.Xml.Linq;

namespace DesktopSonos.Sonos;

public sealed class ZoneMember
{
    public string Uuid { get; init; } = "";
    public string ZoneName { get; init; } = "";
    public IPAddress Ip { get; init; } = IPAddress.None;
    /// <summary>True for bonded satellites (surrounds, subs, second half of a stereo pair).</summary>
    public bool Invisible { get; init; }
}

public sealed class ZoneGroup
{
    public string Id { get; init; } = "";
    public string CoordinatorUuid { get; init; } = "";
    public List<ZoneMember> Members { get; } = new();

    public ZoneMember? Coordinator =>
        Members.FirstOrDefault(m => m.Uuid == CoordinatorUuid);

    /// <summary>"Kitchen" or "Kitchen + 2".</summary>
    public string Label
    {
        get
        {
            var visible = Members.Where(m => !m.Invisible).ToList();
            var head = Coordinator?.ZoneName ?? visible.FirstOrDefault()?.ZoneName ?? "Unknown";
            return visible.Count > 1 ? $"{head} + {visible.Count - 1}" : head;
        }
    }
}

public static class ZoneTopology
{
    /// <summary>
    /// Parses the ZoneGroupState payload returned by ZoneGroupTopology#GetZoneGroupState.
    /// The value is XML-escaped inside the SOAP response, so it arrives here already decoded.
    /// </summary>
    public static List<ZoneGroup> Parse(string zoneGroupStateXml)
    {
        var groups = new List<ZoneGroup>();
        if (string.IsNullOrWhiteSpace(zoneGroupStateXml)) return groups;

        XDocument doc;
        try { doc = XDocument.Parse(zoneGroupStateXml); }
        catch (System.Xml.XmlException) { return groups; }

        // Firmware differs on whether the root is <ZoneGroupState> or <ZoneGroups>.
        foreach (var groupEl in doc.Descendants().Where(e => e.Name.LocalName == "ZoneGroup"))
        {
            var group = new ZoneGroup
            {
                Id = (string?)groupEl.Attribute("ID") ?? "",
                CoordinatorUuid = (string?)groupEl.Attribute("Coordinator") ?? ""
            };

            foreach (var memberEl in groupEl.Descendants().Where(e => e.Name.LocalName == "ZoneGroupMember"))
            {
                var location = (string?)memberEl.Attribute("Location") ?? "";
                var ip = IPAddress.None;
                if (Uri.TryCreate(location, UriKind.Absolute, out var url) &&
                    IPAddress.TryParse(url.Host, out var parsed))
                    ip = parsed;

                group.Members.Add(new ZoneMember
                {
                    Uuid = (string?)memberEl.Attribute("UUID") ?? "",
                    ZoneName = (string?)memberEl.Attribute("ZoneName") ?? "",
                    Ip = ip,
                    Invisible = (string?)memberEl.Attribute("Invisible") == "1"
                });
            }

            if (group.Members.Count > 0)
                groups.Add(group);
        }

        return groups;
    }
}
