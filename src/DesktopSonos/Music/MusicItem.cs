namespace DesktopSonos.Music;

public enum MusicItemKind
{
    Track,
    Album,
    Playlist,
    Artist,

    /// <summary>A saved queue on the players, not Spotify content at all.</summary>
    SonosPlaylist,

    /// <summary>
    /// Something favourited in the Sonos app. It arrives with its own URI and metadata already
    /// signed for the household's account, so it plays with no Spotify sign-in of ours.
    /// </summary>
    Favorite
}

/// <summary>
/// One row in the Spotify panel. Tracks, albums, playlists and saved queues share a single type
/// so the list, the selection and the five queue verbs never have to branch on what is in them —
/// only the enqueue step does.
/// </summary>
public sealed class MusicItem
{
    public MusicItemKind Kind { get; init; }

    /// <summary>Spotify id for Spotify content; the "SQ:3" object id for a saved queue.</summary>
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    /// <summary>Artists for a track or album, the owner for a playlist.</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Album name for a track, track count for a container.</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// Which service it plays from — "Spotify", "YouTube Music", "Radio". Blank for rows that
    /// are not service content, such as a saved queue.
    /// </summary>
    public string Service { get; init; } = "";

    public TimeSpan Duration { get; init; }

    /// <summary>
    /// The URI a saved queue or a favourite is played by. Spotify items build theirs from the id
    /// instead, so this stays empty for them.
    /// </summary>
    public string Uri { get; init; } = "";

    /// <summary>
    /// Ready-made DIDL, used by favourites: theirs already carries the household's own service
    /// token, so it is passed through verbatim rather than rebuilt.
    /// </summary>
    public string Metadata { get; init; } = "";

    /// <summary>
    /// A radio station or other live stream. Sonos will not put one in a queue — it has to be set
    /// as the transport URI — so the queue verbs have to treat it differently.
    /// </summary>
    public bool IsStream { get; init; }

    public string DurationText => Duration > TimeSpan.Zero
        ? Duration.TotalHours >= 1
            ? $"{(int)Duration.TotalHours}:{Duration.Minutes:00}:{Duration.Seconds:00}"
            : $"{Duration.Minutes}:{Duration.Seconds:00}"
        : "";

    public string KindLabel => Kind switch
    {
        MusicItemKind.Track => "track",
        MusicItemKind.Album => "album",
        MusicItemKind.Playlist => "playlist",
        MusicItemKind.Artist => "artist",
        MusicItemKind.Favorite => IsStream ? "station" : "favourite",
        _ => "sonos"
    };

    /// <summary>
    /// What can be opened to show what is inside. Saved queues are included because their
    /// entries carry their own playable URI and metadata — which is the only way to reach single
    /// tracks from a service that cannot be searched, YouTube Music above all.
    /// </summary>
    public bool CanOpen => Kind is MusicItemKind.Album or MusicItemKind.Playlist
        or MusicItemKind.Artist or MusicItemKind.SonosPlaylist;
}
