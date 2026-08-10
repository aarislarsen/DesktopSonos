using System.IO;
using System.Text.Json;
using DesktopSonos.Audio;

namespace DesktopSonos.Persistence;

public sealed class RememberedSpeaker
{
    public string Uuid { get; set; } = "";
    public string Ip { get; set; } = "";
    public string RoomName { get; set; } = "";
}

/// <summary>
/// Everything the app remembers between runs, so the common case needs no setup at all:
/// speakers are drawn from here instantly and only verified in the background, and the music
/// library re-scans itself on startup.
/// </summary>
public sealed class AppSettings
{
    public List<RememberedSpeaker> Speakers { get; set; } = new();
    public List<string> LibraryFolders { get; set; } = new();
    public string? LastRoomUuid { get; set; }

    /// <summary>
    /// Kept stable so media URLs handed to players in an earlier session keep working.
    /// </summary>
    public int MediaServerPort { get; set; } = 8099;
    public bool RoomsExpanded { get; set; } = true;
    public bool LibraryExpanded { get; set; } = true;

    /// <summary>
    /// Minimising hides the window and leaves only the notification-area icon. Set false to get
    /// an ordinary taskbar minimise back.
    /// </summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>
    /// The small always-on-top strip instead of the full window. Remembered so the app comes back
    /// in whichever shape it was left in.
    /// </summary>
    public bool CompactView { get; set; }

    public double CompactWidth { get; set; } = 470;
    public int StreamBitrate { get; set; } = 192;

    /// <summary>Make-up gain in dB for capture, since loopback follows the desktop volume.</summary>
    public int StreamGainDb { get; set; }

    /// <summary>
    /// Output device the captured application gets sent to while streaming, so it plays on Sonos
    /// and not on the PC. Empty leaves the application where it is.
    /// </summary>
    public string RouteDeviceId { get; set; } = "";

    /// <summary>
    /// The last thing that was streamed, so the same setup is ready to go on the next run.
    /// Process ids are not stable across restarts, hence the name.
    /// </summary>
    public bool StreamSourceIsProcess { get; set; }

    public string StreamSourceProcessName { get; set; } = "";
    public string StreamSourceDeviceId { get; set; } = "";

    /// <summary>
    /// Applications currently moved to another output. Written while streaming so a crash cannot
    /// leave an app stuck on a silent device.
    /// </summary>
    public List<RoutedProcess> PendingRoutes { get; set; } = new();
    // ---------------------------------------------------------------- Spotify

    /// <summary>
    /// From the user's own Spotify developer dashboard. Spotify has no shared client id for
    /// third-party desktop apps, so there is nothing sensible to default this to. Not a secret:
    /// the PKCE flow is designed for clients that cannot keep one.
    /// </summary>
    public string SpotifyClientId { get; set; } = "";

    /// <summary>
    /// Loopback port the browser sign-in redirects to. It has to match a redirect URI registered
    /// on the Spotify application, so it is fixed rather than picked at random, and only worth
    /// changing if something else on the machine already owns the port.
    /// </summary>
    public int SpotifyRedirectPort { get; set; } = 8098;

    /// <summary>
    /// Which music service and linked account the players use for Spotify, discovered from the
    /// household on first use and kept so later runs skip discovery. Correct these by hand if a
    /// household somehow reports the wrong one; 0 means "work it out again".
    /// </summary>
    public int SpotifySid { get; set; }

    public int SpotifySn { get; set; }
    public string SpotifyCdudn { get; set; } = "";

    /// <summary>Which of the two side-panel tabs was on screen at exit.</summary>
    public bool SpotifyTabActive { get; set; }

    public double WindowWidth { get; set; } = 1480;
    public double WindowHeight { get; set; } = 880;

    /// <summary>Widths of the resizable side panels; the queue takes whatever is left.</summary>
    public double RoomsWidth { get; set; } = 258;
    public double LibraryWidth { get; set; } = 404;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopSonos");

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file must never stop the app from starting.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Losing preferences is not worth interrupting the user over.
        }
    }
}
