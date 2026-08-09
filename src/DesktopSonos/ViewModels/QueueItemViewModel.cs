using CommunityToolkit.Mvvm.ComponentModel;
using DesktopSonos.Sonos;

namespace DesktopSonos.ViewModels;

public partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(QueueEntry entry)
    {
        Position = entry.Position;
        ObjectId = entry.ObjectId;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? "(untitled)" : entry.Title;
        Artist = entry.Artist;
        Album = entry.Album;
        Duration = entry.Duration;
        Uri = entry.Uri;
    }

    /// <summary>1-based position, which is also what Seek TRACK_NR expects.</summary>
    public int Position { get; }
    public string ObjectId { get; }
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }
    public TimeSpan Duration { get; }

    /// <summary>Needed to rebuild the queue for shuffle and undo.</summary>
    public string Uri { get; }

    public string DurationText =>
        Duration > TimeSpan.Zero
            ? (Duration.TotalHours >= 1 ? Duration.ToString(@"h\:mm\:ss") : Duration.ToString(@"m\:ss"))
            : "";

    [ObservableProperty]
    private bool isNowPlaying;
}
