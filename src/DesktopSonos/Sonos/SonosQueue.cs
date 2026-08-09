using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>One entry as the player reports it from ContentDirectory Browse of "Q:0".</summary>
public sealed class QueueEntry
{
    /// <summary>1-based position in the queue.</summary>
    public int Position { get; init; }
    /// <summary>Opaque id such as "Q:0/7", needed to remove the entry.</summary>
    public string ObjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public string Uri { get; init; } = "";
}

public static class SonosQueue
{
    /// <summary>Reads the whole queue, paging because Browse caps what it returns per call.</summary>
    public static async Task<List<QueueEntry>> ReadAsync(SonosDevice device, int maxEntries = 1000,
        CancellationToken ct = default)
    {
        var entries = new List<QueueEntry>();
        const int pageSize = 100;

        while (entries.Count < maxEntries)
        {
            var (result, returned, total) =
                await device.BrowseAsync("Q:0", entries.Count, pageSize, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(result)) break;

            var page = ParseDidl(result, entries.Count + 1);
            if (page.Count == 0) break;

            entries.AddRange(page);

            if (returned <= 0 || entries.Count >= total) break;
        }

        return entries;
    }

    /// <summary>Parses a DIDL-Lite document into queue entries, numbering from firstPosition.</summary>
    public static List<QueueEntry> ParseDidl(string didl, int firstPosition)
    {
        var entries = new List<QueueEntry>();
        if (string.IsNullOrWhiteSpace(didl)) return entries;

        XDocument doc;
        try { doc = XDocument.Parse(didl); }
        catch (System.Xml.XmlException) { return entries; }

        var position = firstPosition;
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            string Child(string local) =>
                item.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value ?? "";

            var res = item.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
            var durationText = (string?)res?.Attribute("duration");
            TimeSpan.TryParse(durationText, out var duration);

            entries.Add(new QueueEntry
            {
                Position = position++,
                ObjectId = (string?)item.Attribute("id") ?? "",
                Title = Child("title"),
                Artist = Child("creator"),
                Album = Child("album"),
                Duration = duration,
                Uri = res?.Value ?? ""
            });
        }

        return entries;
    }
}
