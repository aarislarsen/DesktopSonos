using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>
/// Parsing for GENA NOTIFY bodies. UPnP wraps changes in an &lt;e:propertyset&gt;; AVTransport
/// and RenderingControl then nest a second, XML-escaped document inside a LastChange property.
/// </summary>
public static class GenaEvents
{
    /// <summary>Top-level properties, keyed by element name.</summary>
    public static Dictionary<string, string> ParsePropertySet(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(xml)) return result;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return result; }

        foreach (var property in doc.Descendants().Where(e => e.Name.LocalName == "property"))
        {
            foreach (var child in property.Elements())
            {
                // Sonos uses both <Name>value</Name> and <Name val="value"/> shapes.
                var value = (string?)child.Attribute("val") ?? child.Value;
                result[child.Name.LocalName] = value;
            }
        }

        return result;
    }

    /// <summary>
    /// Flattens a LastChange document into name/value pairs. Only the Master channel is kept,
    /// so Volume and Mute do not collide with their per-channel siblings.
    /// </summary>
    public static Dictionary<string, string> ParseLastChange(string lastChangeXml)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(lastChangeXml)) return result;

        XDocument doc;
        try { doc = XDocument.Parse(lastChangeXml); }
        catch (System.Xml.XmlException) { return result; }

        foreach (var instance in doc.Descendants().Where(e => e.Name.LocalName == "InstanceID"))
        {
            foreach (var element in instance.Elements())
            {
                var channel = (string?)element.Attribute("channel");
                if (channel != null && !channel.Equals("Master", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = (string?)element.Attribute("val");
                if (value != null) result[element.Name.LocalName] = value;
            }
        }

        return result;
    }
}
