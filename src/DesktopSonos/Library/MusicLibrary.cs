using System.IO;

namespace DesktopSonos.Library;

public sealed class TrackInfo
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public TimeSpan Duration { get; init; }
    public uint TrackNumber { get; init; }

    public string DurationText =>
        Duration > TimeSpan.Zero
            ? (Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss"))
            : "";

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown artist" : Artist!;
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "" : Album!;
}

public static class MusicLibrary
{
    /// <summary>Formats Sonos players can decode natively.</summary>
    public static readonly string[] Extensions =
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".wav", ".wma", ".ogg", ".aif", ".aiff", ".alac"
    };

    private static readonly HashSet<string> ExtensionSet =
        new(Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Walks a folder (local path or UNC share) and reads tags. Reports each batch as it
    /// goes so a large NAS scan populates the UI progressively.
    /// </summary>
    public static Task<List<TrackInfo>> ScanAsync(
        string rootPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var results = new List<TrackInfo>();

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
                ReturnSpecialDirectories = false
            };

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(rootPath, "*", options); }
            catch (Exception ex)
            {
                progress?.Report($"Cannot read {rootPath}: {ex.Message}");
                return results;
            }

            var scanned = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!ExtensionSet.Contains(Path.GetExtension(file))) continue;

                results.Add(ReadTrack(file));

                if (++scanned % 100 == 0)
                    progress?.Report($"Scanned {scanned} files in {rootPath}...");
            }

            progress?.Report($"Found {results.Count} tracks in {rootPath}.");
            return results;
        }, ct);
    }

    /// <summary>Reads tags, falling back to the file name when the file has none or is unreadable.</summary>
    public static TrackInfo ReadTrack(string path)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;

            var title = string.IsNullOrWhiteSpace(tag.Title) ? fallbackTitle : tag.Title;
            var artist = tag.FirstPerformer ?? tag.FirstAlbumArtist;

            return new TrackInfo
            {
                Path = path,
                Title = title,
                Artist = artist,
                Album = tag.Album,
                Duration = file.Properties?.Duration ?? TimeSpan.Zero,
                TrackNumber = tag.Track
            };
        }
        catch
        {
            // Corrupt tags, exotic containers, or a share that went away mid-scan.
            return new TrackInfo { Path = path, Title = fallbackTitle };
        }
    }
}
