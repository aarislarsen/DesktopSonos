using System.IO;
using System.Text.Json;
using DesktopSonos.Library;

namespace DesktopSonos.Persistence;

/// <summary>
/// On-disk copy of the scanned library, so the track list is on screen instantly at startup
/// instead of after a full re-scan of a NAS share. The scan still runs in the background and
/// replaces this once it finishes.
/// </summary>
public static class LibraryCache
{
    private sealed class CachedTrack
    {
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public long DurationTicks { get; set; }
        public uint TrackNumber { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string FilePath => Path.Combine(AppSettings.DirectoryPath, "library.json");

    public static List<TrackInfo> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<TrackInfo>();

            var json = File.ReadAllText(FilePath);
            var cached = JsonSerializer.Deserialize<List<CachedTrack>>(json, Options);
            if (cached is null) return new List<TrackInfo>();

            return cached
                .Where(c => !string.IsNullOrWhiteSpace(c.Path))
                .Select(c => new TrackInfo
                {
                    Path = c.Path,
                    Title = string.IsNullOrWhiteSpace(c.Title)
                        ? Path.GetFileNameWithoutExtension(c.Path)
                        : c.Title,
                    Artist = c.Artist,
                    Album = c.Album,
                    Duration = TimeSpan.FromTicks(c.DurationTicks),
                    TrackNumber = c.TrackNumber
                })
                .ToList();
        }
        catch
        {
            // A corrupt cache is not worth surfacing; the background scan rebuilds it.
            return new List<TrackInfo>();
        }
    }

    public static void Save(IEnumerable<TrackInfo> tracks)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DirectoryPath);

            var cached = tracks.Select(t => new CachedTrack
            {
                Path = t.Path,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                DurationTicks = t.Duration.Ticks,
                TrackNumber = t.TrackNumber
            }).ToList();

            File.WriteAllText(FilePath, JsonSerializer.Serialize(cached, Options));
        }
        catch
        {
            // Losing the cache only costs a re-scan next time.
        }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch { }
    }
}
