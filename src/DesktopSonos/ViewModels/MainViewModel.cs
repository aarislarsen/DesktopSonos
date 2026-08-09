using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopSonos.Audio;
using DesktopSonos.Library;
using DesktopSonos.Persistence;
using DesktopSonos.Serving;
using DesktopSonos.Sonos;

namespace DesktopSonos.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Enqueuing is one SOAP round trip per track; keep a sane ceiling.</summary>
    private const int MaxQueueLength = 500;

    private readonly HttpMediaServer _server = new();
    private readonly LoopbackStreamer _streamer = new();
    private readonly AppAudioRouter _router = new();
    private readonly GenaSubscriber _gena;
    private readonly Dictionary<string, SonosDevice> _devicesByUuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrackInfo> _allTracks = new();
    private readonly DispatcherTimer _tick;
    private readonly Stack<UndoStep> _undo = new();
    private readonly Random _random = new();

    private AppSettings _settings = new();
    private CancellationTokenSource? _scanCts;
    private bool _tickBusy;
    private int _tickCount;
    private bool _userIsSeeking;
    private int _currentTrackNumber;
    private string _subscribedCoordinator = "";
    private string _subscribedRoom = "";
    private bool _relinkChecked;
    private bool _settingsReady;
    private CancellationTokenSource? _switchCts;
    private bool _suppressSourceSwitch;

    private sealed record UndoStep(string Description, Func<Task> Apply);

    /// <summary>Enough to put a queue back exactly as it was.</summary>
    private sealed record QueueSnapshotItem(string Uri, string Title, string Artist, string Album,
        TimeSpan Duration);

    public MainViewModel()
    {
        _server.Log += AppendLog;
        _server.Streamer = _streamer;
        _streamer.Log += AppendLog;
        _router.Log += AppendLog;

        _gena = new GenaSubscriber(_server);
        _gena.Log += AppendLog;
        _gena.Notified += OnPlayerEvent;

        RefreshAudioSources();

        // Windows persists per-application audio routing, so it has to be undone even if the
        // window never gets a clean Closed event.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += async (_, _) => await TickAsync();
    }

    // ================================================================ state

    public ObservableCollection<SpeakerViewModel> Rooms { get; } = new();
    public ObservableCollection<TrackInfo> Tracks { get; } = new();
    public ObservableCollection<QueueItemViewModel> Queue { get; } = new();
    public ObservableCollection<string> LibraryFolders { get; } = new();
    public ObservableCollection<AudioSourceOption> AudioSources { get; } = new();
    public ObservableCollection<RenderDeviceOption> RouteTargets { get; } = new();

    [ObservableProperty] private SpeakerViewModel? selectedRoom;
    [ObservableProperty] private TrackInfo? selectedTrack;
    [ObservableProperty] private QueueItemViewModel? selectedQueueItem;
    [ObservableProperty] private AudioSourceOption? selectedAudioSource;

    /// <summary>
    /// Where the captured application is sent while streaming. Pointing it at an output nothing is
    /// plugged into is what makes the sound leave the PC entirely and come out of Sonos only.
    /// </summary>
    [ObservableProperty] private RenderDeviceOption? selectedRouteTarget;

    /// <summary>Routing applies to an application, not to a whole output device.</summary>
    public bool CanRouteApp =>
        AppAudioRouter.IsSupported && SelectedAudioSource?.Kind == AudioSourceKind.Process;

    partial void OnSelectedAudioSourceChanged(AudioSourceOption? value)
    {
        OnPropertyChanged(nameof(CanRouteApp));
        RememberStreamSource(value);
        QueueSourceSwitch();
    }

    /// <summary>
    /// Keeps the source ready for next time. Paired with <see cref="AppSettings.RouteDeviceId"/>
    /// this means "app X out of output Y" survives a restart, even though X's process id will not.
    /// </summary>
    private void RememberStreamSource(AudioSourceOption? value)
    {
        if (!_settingsReady || value is null) return;

        _settings.StreamSourceIsProcess = value.Kind == AudioSourceKind.Process;
        _settings.StreamSourceProcessName = value.Kind == AudioSourceKind.Process ? value.ProcessName : "";
        _settings.StreamSourceDeviceId = value.Kind == AudioSourceKind.Device ? value.DeviceId : "";
        _settings.Save();
    }

    partial void OnSelectedRouteTargetChanged(RenderDeviceOption? value)
    {
        // The first selection happens while building the lists in the constructor, before the
        // settings file has been read; saving then would overwrite it with defaults.
        if (!_settingsReady) return;

        _settings.RouteDeviceId = value?.Id ?? "";
        _settings.Save();

        QueueSourceSwitch();
    }

    [ObservableProperty] private int streamBitrate = 192;

    /// <summary>0-36 dB make-up gain, to offset a low Windows volume on the captured endpoint.</summary>
    [ObservableProperty] private int streamGainDb;

    public string StreamGainText => StreamGainDb == 0 ? "0 dB" : $"+{StreamGainDb} dB";

    partial void OnStreamGainDbChanged(int value)
    {
        _streamer.GainLinear = (float)Math.Pow(10, Math.Clamp(value, 0, 36) / 20.0);
        OnPropertyChanged(nameof(StreamGainText));
        _settings.StreamGainDb = value;
        _settings.Save();
    }

    [ObservableProperty] private string status = "Starting up…";
    [ObservableProperty] private string logText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isLiveEventing;

    [ObservableProperty] private string nowPlaying = "Nothing playing";
    [ObservableProperty] private string nowPlayingDetail = "";
    [ObservableProperty] private string transportState = "STOPPED";
    [ObservableProperty] private double positionSeconds;
    [ObservableProperty] private double durationSeconds;
    [ObservableProperty] private string positionText = "0:00";
    [ObservableProperty] private string durationText = "0:00";

    [ObservableProperty] private bool isDesktopStreaming;

    /// <summary>Live capture level, so a silent source is obvious without reading the log.</summary>
    [ObservableProperty] private string captureLevelText = "";
    [ObservableProperty] private string manualIp = "";
    [ObservableProperty] private string networkPath = @"\\nas\music";
    [ObservableProperty] private string shareUser = "";
    [ObservableProperty] private string filterText = "";
    [ObservableProperty] private bool isSettingsOpen;
    [ObservableProperty] private string libraryStatus = "";

    /// <summary>
    /// Collapsing the room list also makes it impossible to change the playing room by a stray
    /// click, which is the main reason to hide it.
    /// </summary>
    [ObservableProperty] private bool isRoomsExpanded = true;

    /// <summary>The library collapses the same way, so a small window can show just the queue.</summary>
    [ObservableProperty] private bool isLibraryExpanded = true;

    /// <summary>
    /// The compact strip: transport, what is playing and volume, in a window small enough to keep
    /// on screen all day. Everything else — library, queue, rooms — is one click away.
    /// </summary>
    [ObservableProperty] private bool isCompactView;

    partial void OnIsCompactViewChanged(bool value)
    {
        if (!_settingsReady) return;
        _settings.CompactView = value;
        _settings.Save();
    }

    [RelayCommand]
    private void ToggleCompactView() => IsCompactView = !IsCompactView;

    partial void OnIsRoomsExpandedChanged(bool value)
    {
        if (!_settingsReady) return;
        _settings.RoomsExpanded = value;
        _settings.Save();
    }

    partial void OnIsLibraryExpandedChanged(bool value)
    {
        if (!_settingsReady) return;
        _settings.LibraryExpanded = value;
        _settings.Save();
    }

    [RelayCommand]
    private void ToggleRooms() => IsRoomsExpanded = !IsRoomsExpanded;

    [RelayCommand]
    private void ToggleLibrary() => IsLibraryExpanded = !IsLibraryExpanded;

    private List<TrackInfo> _selectedTracks = new();

    public bool IsPlaying => TransportState == "PLAYING";
    public string PlayGlyph => IsPlaying ? "❚❚" : "▶";
    public string TrackCountText => $"{Tracks.Count} of {_allTracks.Count}";
    public string QueueCountText => Queue.Count == 0 ? "empty" : $"{Queue.Count} tracks";
    public string RoomLabel => Coordinator?.RoomName ?? SelectedRoom?.RoomName ?? "No room selected";
    public int RoomVolume => SelectedRoom?.Volume ?? 0;

    public bool CanUndo => _undo.Count > 0;
    public string UndoDescription => _undo.Count > 0 ? $"Undo {_undo.Peek().Description}" : "Nothing to undo";

    /// <summary>Set by the view so credentials never live in the view model.</summary>
    public Func<string, string, string?>? PromptForPassword { get; set; }

    public void SetSelectedTracks(IEnumerable<TrackInfo> tracks)
    {
        _selectedTracks = tracks.ToList();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public string SelectionSummary => _selectedTracks.Count > 1 ? $"{_selectedTracks.Count} selected" : "";

    private List<TrackInfo> EffectiveSelection =>
        _selectedTracks.Count > 0
            ? _selectedTracks
            : SelectedTrack is null ? new List<TrackInfo>() : new List<TrackInfo> { SelectedTrack };

    /// <summary>The player that owns playback for the selected room's group.</summary>
    private SonosDevice? Coordinator
    {
        get
        {
            if (SelectedRoom is null) return null;
            return _devicesByUuid.TryGetValue(SelectedRoom.CoordinatorUuid, out var coordinator)
                ? coordinator
                : SelectedRoom.Device;
        }
    }

    partial void OnSelectedRoomChanged(SpeakerViewModel? value)
    {
        OnPropertyChanged(nameof(RoomLabel));
        OnPropertyChanged(nameof(RoomVolume));
        if (value is null) return;

        _settings.LastRoomUuid = value.Uuid;
        _settings.Save();

        _ = LoadQueueAsync();
        _ = SyncSubscriptionsAsync();
        _ = TickAsync();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnStreamBitrateChanged(int value) => _streamer.BitrateKbps = Math.Clamp(value, 96, 320);
    partial void OnTransportStateChanged(string value)
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayGlyph));
    }

    // ================================================================ startup

    public void StartServices()
    {
        _settings = AppSettings.Load();
        _settingsReady = true;

        StreamBitrate = _settings.StreamBitrate;
        StreamGainDb = _settings.StreamGainDb;
        IsRoomsExpanded = _settings.RoomsExpanded;
        IsLibraryExpanded = _settings.LibraryExpanded;
        IsCompactView = _settings.CompactView;

        RestoreStreamSelection();

        // A crash while streaming would otherwise leave an application permanently pointed at an
        // output the user cannot hear.
        if (_settings.PendingRoutes.Count > 0)
        {
            var recovered = _router.RecoverPending(_settings.PendingRoutes);
            AppendLog($"Put {recovered} application(s) back on their normal output device after " +
                      "an unclean shutdown.");
            _settings.PendingRoutes.Clear();
            _settings.Save();
        }

        try
        {
            // Reusing last session's port matters: players still hold media URLs that name it.
            _server.Start(_settings.MediaServerPort > 0 ? _settings.MediaServerPort : 8099);
            _settings.MediaServerPort = _server.Port;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Status = $"Could not start the media server: {ex.Message}";
            return;
        }

        _tick.Start();

        foreach (var folder in _settings.LibraryFolders)
            LibraryFolders.Add(folder);

        LoadCachedLibrary();
        RestoreRememberedSpeakers();

        _ = VerifySpeakersAsync();
        _ = RefreshLibraryInBackgroundAsync();
    }

    /// <summary>
    /// Restores last session's streaming setup: the same output to send the app to, and the same
    /// app if it is running again. Nothing starts on its own — this only pre-selects it.
    /// </summary>
    private void RestoreStreamSelection()
    {
        _suppressSourceSwitch = true;
        try
        {
            SelectedRouteTarget = RouteTargets.FirstOrDefault(t => t.Id == _settings.RouteDeviceId)
                                  ?? RouteTargets.FirstOrDefault();

            var remembered = _settings.StreamSourceIsProcess
                ? AudioSources.FirstOrDefault(o =>
                    o.Kind == AudioSourceKind.Process &&
                    string.Equals(o.ProcessName, _settings.StreamSourceProcessName,
                        StringComparison.OrdinalIgnoreCase))
                : AudioSources.FirstOrDefault(o =>
                    o.Kind == AudioSourceKind.Device && o.DeviceId == _settings.StreamSourceDeviceId);

            if (remembered != null) SelectedAudioSource = remembered;
        }
        finally { _suppressSourceSwitch = false; }
    }

    /// <summary>
    /// Puts last session's tracks on screen with no scanning, and — just as importantly —
    /// re-registers every path so media URLs already sitting in a player's queue resolve again.
    /// </summary>
    private void LoadCachedLibrary()
    {
        var cached = LibraryCache.Load();
        if (cached.Count == 0) return;

        _allTracks.AddRange(cached);
        _server.Registry.RegisterAll(_allTracks.Select(t => t.Path));
        ApplyFilter();

        Status = $"{_allTracks.Count} tracks remembered — checking the folders for changes…";
    }

    /// <summary>
    /// Puts the rooms on screen immediately from the last session, before any network traffic.
    /// The usual case is that nothing has changed, so discovery never needs to be visible.
    /// </summary>
    private void RestoreRememberedSpeakers()
    {
        if (_settings.Speakers.Count == 0)
        {
            Status = "No speakers remembered yet — searching…";
            return;
        }

        foreach (var remembered in _settings.Speakers)
        {
            if (!IPAddress.TryParse(remembered.Ip, out var ip)) continue;
            var device = new SonosDevice(ip, remembered.Uuid, remembered.RoomName);
            _devicesByUuid[remembered.Uuid] = device;
            Rooms.Add(new SpeakerViewModel(device, remembered.Uuid, 1) { GroupDescription = "Remembered" });
        }

        SelectedRoom = Rooms.FirstOrDefault(r => r.Uuid == _settings.LastRoomUuid) ?? Rooms.FirstOrDefault();
        Status = $"{Rooms.Count} room(s) from last time — checking they are still there…";
    }

    /// <summary>Confirms the remembered speakers in the background, falling back to SSDP.</summary>
    private async Task VerifySpeakersAsync()
    {
        foreach (var remembered in _settings.Speakers.ToList())
        {
            if (!IPAddress.TryParse(remembered.Ip, out var ip)) continue;
            try
            {
                var state = await _devicesByUuid[remembered.Uuid].GetZoneGroupStateAsync();
                if (ZoneTopology.Parse(state).Count > 0)
                {
                    await LoadTopologyFromAnyAsync(new[] { ip }, quiet: true);
                    return;
                }
            }
            catch
            {
                // Try the next remembered speaker before falling back to a full search.
            }
        }

        await DiscoverAsync();
    }

    /// <summary>
    /// Re-reads every remembered folder off the UI thread, then swaps the whole list in one go.
    /// The cached tracks stay usable throughout, so playback never has to wait for this.
    /// </summary>
    private async Task RefreshLibraryInBackgroundAsync()
    {
        if (LibraryFolders.Count == 0) return;

        _scanCts ??= new CancellationTokenSource();
        LibraryStatus = "refreshing…";

        var found = new List<TrackInfo>();
        foreach (var folder in LibraryFolders.ToList())
        {
            try
            {
                found.AddRange(await MusicLibrary.ScanAsync(folder, null, _scanCts.Token));
            }
            catch (OperationCanceledException)
            {
                LibraryStatus = "";
                return;
            }
            catch (Exception ex)
            {
                AppendLog($"Scan of {folder} failed: {ex.Message}");
            }
        }

        LibraryStatus = "";

        if (found.Count == 0)
        {
            if (_allTracks.Count > 0)
                Status = "Could not reach the library folders — using the remembered list.";
            return;
        }

        _allTracks.Clear();
        _allTracks.AddRange(found);
        SortTracks();
        _server.Registry.RegisterAll(_allTracks.Select(t => t.Path));
        ApplyFilter();

        LibraryCache.Save(_allTracks);
        Status = $"Library up to date — {_allTracks.Count} tracks.";
    }

    // ================================================================ discovery

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        IsBusy = true;
        Status = "Searching for Sonos players…";
        try
        {
            var addresses = await SsdpDiscovery.FindZonePlayersAsync(TimeSpan.FromSeconds(3));
            if (addresses.Count == 0)
            {
                Status = "No players answered. Check this PC is on the same subnet as Sonos, " +
                         "that the network is Private, and that Windows Firewall allows this app.";
                return;
            }

            await LoadTopologyFromAnyAsync(addresses);
        }
        catch (Exception ex)
        {
            Status = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddByIpAsync()
    {
        if (!IPAddress.TryParse(ManualIp.Trim(), out var ip))
        {
            Status = "Enter a valid IPv4 address, for example 192.168.1.42.";
            return;
        }

        IsBusy = true;
        try { await LoadTopologyFromAnyAsync(new[] { ip }); }
        finally { IsBusy = false; }
    }

    private async Task LoadTopologyFromAnyAsync(IReadOnlyList<IPAddress> candidates, bool quiet = false)
    {
        SonosDevice? seed = null;
        foreach (var ip in candidates)
        {
            seed = await SonosDevice.LoadAsync(ip);
            if (seed != null) break;
        }

        if (seed is null)
        {
            if (!quiet) Status = "Found something on the network, but it did not answer as a Sonos player.";
            return;
        }

        List<ZoneGroup> groups;
        try { groups = ZoneTopology.Parse(await seed.GetZoneGroupStateAsync()); }
        catch (Exception ex)
        {
            AppendLog($"Topology query failed: {ex.Message}");
            groups = new List<ZoneGroup>();
        }

        var previousUuid = SelectedRoom?.Uuid ?? _settings.LastRoomUuid;
        _devicesByUuid.Clear();
        Rooms.Clear();

        if (groups.Count == 0)
        {
            _devicesByUuid[seed.Uuid] = seed;
            Rooms.Add(new SpeakerViewModel(seed, seed.Uuid, 1) { GroupDescription = "Standalone" });
        }
        else
        {
            foreach (var group in groups)
            {
                var visible = group.Members.Where(m => !m.Invisible).ToList();
                foreach (var member in visible)
                {
                    var device = new SonosDevice(member.Ip, member.Uuid, member.ZoneName);
                    _devicesByUuid[member.Uuid] = device;

                    var isCoordinator = member.Uuid == group.CoordinatorUuid;
                    var coordinatorName = visible.FirstOrDefault(m => m.Uuid == group.CoordinatorUuid)?.ZoneName;

                    Rooms.Add(new SpeakerViewModel(device, group.CoordinatorUuid, visible.Count)
                    {
                        GroupDescription = visible.Count <= 1
                            ? "Standalone"
                            : isCoordinator ? $"Leading {visible.Count} rooms" : $"Grouped with {coordinatorName}"
                    });
                }

                foreach (var hidden in group.Members.Where(m => m.Invisible))
                    _devicesByUuid.TryAdd(hidden.Uuid, new SonosDevice(hidden.Ip, hidden.Uuid, hidden.ZoneName));
            }
        }

        SelectedRoom = Rooms.FirstOrDefault(r => r.Uuid == previousUuid) ?? Rooms.FirstOrDefault();
        RememberSpeakers();

        Status = $"{Rooms.Count} room(s) ready.";
        await RefreshVolumesAsync();
        await SyncSubscriptionsAsync();
    }

    private void RememberSpeakers()
    {
        _settings.Speakers = Rooms
            .Select(r => new RememberedSpeaker { Uuid = r.Uuid, Ip = r.Address, RoomName = r.RoomName })
            .ToList();
        _settings.LastRoomUuid = SelectedRoom?.Uuid;
        _settings.Save();
    }

    private async Task RefreshVolumesAsync()
    {
        foreach (var room in Rooms.ToList())
        {
            try { room.SetVolumeFromDevice(await room.Device.GetVolumeAsync()); }
            catch { /* a player can drop off between discovery and this call */ }
        }
        OnPropertyChanged(nameof(RoomVolume));
    }

    // ================================================================ eventing

    private async Task SyncSubscriptionsAsync()
    {
        var coordinator = Coordinator;
        var room = SelectedRoom?.Device;
        if (coordinator is null) return;

        if (coordinator.Uuid == _subscribedCoordinator && (room?.Uuid ?? "") == _subscribedRoom)
            return;

        _subscribedCoordinator = coordinator.Uuid;
        _subscribedRoom = room?.Uuid ?? "";

        await _gena.ResubscribeAsync(coordinator, room);
        IsLiveEventing = _gena.HasSubscriptions;
    }

    /// <summary>Raised on a background thread by the HTTP server; marshal before touching state.</summary>
    private void OnPlayerEvent(string serviceName, string body)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        app.Dispatcher.BeginInvoke(() =>
        {
            switch (serviceName)
            {
                case "Queue":
                    // Any queue notification means it changed; re-read rather than guess.
                    _ = LoadQueueAsync();
                    break;

                case "AVTransport":
                    ApplyLastChange(body, transport: true);
                    break;

                case "RenderingControl":
                    ApplyLastChange(body, transport: false);
                    break;
            }
        });
    }

    private void ApplyLastChange(string body, bool transport)
    {
        var properties = GenaEvents.ParsePropertySet(body);
        if (!properties.TryGetValue("LastChange", out var lastChange)) return;

        var values = GenaEvents.ParseLastChange(lastChange);

        if (transport)
        {
            if (values.TryGetValue("TransportState", out var state)) TransportState = state;

            if (values.TryGetValue("CurrentTrackMetaData", out var metadata))
            {
                var title = Didl.TitleFrom(metadata);
                if (!string.IsNullOrWhiteSpace(title)) NowPlaying = title!;
            }

            if (values.TryGetValue("CurrentTrack", out var trackText) &&
                int.TryParse(trackText, out var trackNumber))
            {
                _currentTrackNumber = trackNumber;
                MarkNowPlaying(trackNumber);
            }

            if (values.TryGetValue("CurrentTrackDuration", out var durationValue))
            {
                var duration = ParseTime(durationValue);
                DurationSeconds = duration.TotalSeconds;
                DurationText = duration > TimeSpan.Zero ? Format(duration) : "live";
            }
        }
        else
        {
            if (values.TryGetValue("Volume", out var volumeText) &&
                int.TryParse(volumeText, out var volume))
            {
                SelectedRoom?.SetVolumeFromDevice(volume);
                OnPropertyChanged(nameof(RoomVolume));
            }

            if (values.TryGetValue("Mute", out var muteText) && SelectedRoom != null)
                SelectedRoom.SetMuteFromDevice(muteText == "1");
        }
    }

    // ================================================================ library

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a music folder" };
        if (dialog.ShowDialog() != true) return;
        await AddLibraryPathAsync(dialog.FolderName);
    }

    [RelayCommand]
    private async Task AddNetworkPathAsync()
    {
        var path = NetworkPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = @"Enter a UNC path such as \\nas\music.";
            return;
        }

        if (NetworkShare.IsUnc(path) && !string.IsNullOrWhiteSpace(ShareUser))
        {
            var password = PromptForPassword?.Invoke(NetworkShare.GetShareRoot(path) ?? path, ShareUser);
            if (password is null) return;

            var error = NetworkShare.Connect(path, ShareUser, password);
            if (error != null)
            {
                Status = error;
                return;
            }
        }

        if (!Directory.Exists(path))
        {
            Status = $"Cannot reach {path}. Check the share name and your permissions.";
            return;
        }

        await AddLibraryPathAsync(path);
    }

    private async Task AddLibraryPathAsync(string path)
    {
        if (LibraryFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Status = "That folder is already in the library.";
            return;
        }

        LibraryFolders.Add(path);
        _settings.LibraryFolders = LibraryFolders.ToList();
        _settings.Save();

        await ScanFolderAsync(path, quiet: false);
    }

    [RelayCommand]
    private Task RefreshLibraryAsync() => RefreshLibraryInBackgroundAsync();

    private async Task ScanFolderAsync(string path, bool quiet)
    {
        _scanCts ??= new CancellationTokenSource();

        if (!quiet) IsBusy = true;
        try
        {
            var progress = quiet ? null : new Progress<string>(message => Status = message);
            var found = await MusicLibrary.ScanAsync(path, progress, _scanCts.Token);

            _allTracks.AddRange(found);
            SortTracks();
            _server.Registry.RegisterAll(found.Select(t => t.Path));
            ApplyFilter();
            LibraryCache.Save(_allTracks);

            if (!quiet) Status = $"Library now holds {_allTracks.Count} tracks.";
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Status = $"Scan of {path} failed: {ex.Message}";
        }
        finally
        {
            if (!quiet) IsBusy = false;
        }
    }

    private void SortTracks()
    {
        _allTracks.Sort((a, b) =>
        {
            var byArtist = string.Compare(a.DisplayArtist, b.DisplayArtist, StringComparison.OrdinalIgnoreCase);
            if (byArtist != 0) return byArtist;
            var byAlbum = string.Compare(a.DisplayAlbum, b.DisplayAlbum, StringComparison.OrdinalIgnoreCase);
            if (byAlbum != 0) return byAlbum;
            if (a.TrackNumber != b.TrackNumber) return a.TrackNumber.CompareTo(b.TrackNumber);
            return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void ForgetLibrary()
    {
        _allTracks.Clear();
        LibraryFolders.Clear();
        _settings.LibraryFolders.Clear();
        _settings.Save();
        LibraryCache.Delete();
        _server.Registry.Clear();
        ApplyFilter();
        Status = "Library cleared.";
    }

    private void ApplyFilter()
    {
        Tracks.Clear();
        var needle = FilterText?.Trim();

        IEnumerable<TrackInfo> source = _allTracks;
        if (!string.IsNullOrEmpty(needle))
        {
            source = source.Where(t =>
                t.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (t.Artist?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Album?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        foreach (var track in source.Take(5000)) Tracks.Add(track);
        OnPropertyChanged(nameof(TrackCountText));
    }

    // ================================================================ playback

    [RelayCommand]
    private Task PlayNowAsync()
    {
        var selection = EffectiveSelection;
        if (selection.Count == 0)
        {
            Status = "Pick one or more tracks first.";
            return Task.CompletedTask;
        }
        return PlayTracksAsync(selection, 0);
    }

    [RelayCommand]
    private Task PlayAllAsync() => PlayTracksAsync(Tracks.ToList(), 0);

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null) { Status = "Select a room first."; return; }

        var selection = EffectiveSelection;
        if (selection.Count == 0) { Status = "Pick one or more tracks first."; return; }

        IsBusy = true;
        try
        {
            foreach (var track in selection.Take(MaxQueueLength))
            {
                var url = _server.BuildFileUrl(track.Path, coordinator.Ip);
                await coordinator.AddUriToQueueAsync(url,
                    Didl.ForTrack(track.Title, track.Artist, track.Album, url, track.Duration));
            }

            await LoadQueueAsync();
            Status = $"Added {selection.Count} track(s) to {coordinator.RoomName}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not add to the queue: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Fills the queue with tracks drawn at random from the library. A Sonos queue holds at most
    /// 500 entries, so a large library cannot be queued whole and shuffled on the speaker — drawing
    /// a fresh random selection is the practical equivalent of shuffling everything.
    /// </summary>
    [RelayCommand]
    private async Task ShuffleLibraryAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null) { Status = "Select a room first."; return; }

        // A search filter narrows what gets drawn from. With no filter the whole library is fair
        // game, including the tracks past the 5000 the list itself displays.
        var pool = string.IsNullOrWhiteSpace(FilterText) ? _allTracks.ToList() : Tracks.ToList();
        if (pool.Count == 0) { Status = "Nothing in the library to shuffle."; return; }

        var room = MaxQueueLength - Queue.Count;
        if (room <= 0)
        {
            Status = $"The queue is already at the {MaxQueueLength}-track limit — clear it first.";
            return;
        }

        // Partial Fisher-Yates: only as many draws as there are slots to fill, and no track can
        // come up twice.
        var take = Math.Min(room, pool.Count);
        for (var i = 0; i < take; i++)
        {
            var j = Random.Shared.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        IsBusy = true;
        try
        {
            Status = $"Adding {take} random track(s) to {coordinator.RoomName}…";
            for (var i = 0; i < take; i++)
            {
                var track = pool[i];
                var url = _server.BuildFileUrl(track.Path, coordinator.Ip);
                await coordinator.AddUriToQueueAsync(url,
                    Didl.ForTrack(track.Title, track.Artist, track.Album, url, track.Duration));
            }

            await LoadQueueAsync();
            Status = $"Added {take} random track(s) to {coordinator.RoomName}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not shuffle the library: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    public async Task PlayTracksAsync(IReadOnlyList<TrackInfo> tracks, int startIndex)
    {
        var coordinator = Coordinator;
        if (coordinator is null) { Status = "Select a room first."; return; }
        if (tracks.Count == 0) { Status = "Nothing to play — add a folder to the library."; return; }

        if (IsDesktopStreaming) await StopDesktopStreamAsync();

        var queue = tracks.Take(MaxQueueLength).ToList();
        if (startIndex >= queue.Count) startIndex = 0;

        IsBusy = true;
        try
        {
            try
            {
                await QueueAndPlayAsync(coordinator, queue, startIndex);
            }
            catch (SonosException ex) when (ex.ErrorCode is 701 or 800)
            {
                AppendLog($"{coordinator.RoomName} refused playback ({ex.ErrorCode}); re-reading topology.");
                await LoadTopologyFromAnyAsync(new[] { coordinator.Ip });

                var refreshed = Coordinator;
                if (refreshed is null) throw;
                await QueueAndPlayAsync(refreshed, queue, startIndex);
                coordinator = refreshed;
            }

            await LoadQueueAsync();
            Status = $"Playing on {coordinator.RoomName}.";
        }
        catch (Exception ex)
        {
            Status = $"Playback failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private async Task QueueAndPlayAsync(SonosDevice coordinator, List<TrackInfo> queue, int startIndex)
    {
        Status = $"Queuing {queue.Count} track(s) on {coordinator.RoomName}…";

        await coordinator.ClearQueueAsync();
        foreach (var track in queue)
        {
            var url = _server.BuildFileUrl(track.Path, coordinator.Ip);
            await coordinator.AddUriToQueueAsync(url,
                Didl.ForTrack(track.Title, track.Artist, track.Album, url, track.Duration));
        }

        await coordinator.PlayFromQueueAsync();

        // Switching the transport source is not instant; Play too soon comes back as 701.
        await Task.Delay(250);

        if (startIndex > 0)
            await coordinator.SeekAsync("TRACK_NR", (startIndex + 1).ToString());

        await coordinator.PlayWithRetryAsync();
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null) { Status = "Select a room first."; return; }

        try
        {
            if (IsPlaying) { await coordinator.PauseAsync(); return; }

            // This button resumes; it cannot start from nothing. An empty queue answers 701,
            // so check first and load the current library selection instead of failing.
            if (await coordinator.GetLoadedTrackCountAsync() == 0)
            {
                if (Tracks.Count == 0)
                {
                    Status = $"Nothing is loaded on {coordinator.RoomName}, and the library is empty.";
                    return;
                }

                var startIndex = SelectedTrack is null ? 0 : Math.Max(0, Tracks.IndexOf(SelectedTrack));
                await PlayTracksAsync(Tracks.ToList(), startIndex);
                return;
            }

            await coordinator.PlayWithRetryAsync();
        }
        catch (SonosException ex) when (ex.ErrorCode == 701)
        {
            Status = $"{coordinator.RoomName} would not start — {SonosSoap.UpnpErrorText(701)}.";
        }
        catch (Exception ex)
        {
            Status = $"Transport command failed: {ex.Message}";
        }
    }

    [RelayCommand] private Task StopPlaybackAsync() => TransportAsync(d => d.StopAsync());
    [RelayCommand] private Task NextAsync() => TransportAsync(d => d.NextAsync());
    [RelayCommand] private Task PreviousAsync() => TransportAsync(d => d.PreviousAsync());

    private async Task TransportAsync(Func<SonosDevice, Task> action)
    {
        var coordinator = Coordinator;
        if (coordinator is null) return;

        try { await action(coordinator); }
        catch (SonosException ex) when (ex.ErrorCode is 701 or 711 or 718)
        {
            Status = $"{coordinator.RoomName} has nothing loaded to act on.";
        }
        catch (Exception ex)
        {
            Status = $"Transport command failed: {ex.Message}";
        }
    }

    public void BeginSeek() => _userIsSeeking = true;

    public async Task CommitSeekAsync(double seconds)
    {
        _userIsSeeking = false;
        var coordinator = Coordinator;
        if (coordinator is null || DurationSeconds <= 0) return;

        try
        {
            var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds));
            await coordinator.SeekAsync("REL_TIME", target.ToString(@"h\:mm\:ss"));
        }
        catch (Exception ex)
        {
            Status = $"Seek failed: {ex.Message}";
        }
    }

    public async Task SetVolumeAsync(int volume)
    {
        if (SelectedRoom is null) return;
        SelectedRoom.Volume = Math.Clamp(volume, 0, 100);
        OnPropertyChanged(nameof(RoomVolume));
        await Task.CompletedTask;
    }

    public Task NudgeVolumeAsync(int delta) => SetVolumeAsync(RoomVolume + delta);

    // ================================================================ queue

    [RelayCommand]
    private Task RefreshQueueAsync() => LoadQueueAsync();

    private async Task LoadQueueAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null)
        {
            Queue.Clear();
            OnPropertyChanged(nameof(QueueCountText));
            return;
        }

        try
        {
            var entries = await SonosQueue.ReadAsync(coordinator, MaxQueueLength);
            var keepSelected = SelectedQueueItem?.Position;

            Queue.Clear();
            foreach (var entry in entries) Queue.Add(new QueueItemViewModel(entry));

            MarkNowPlaying(_currentTrackNumber);

            if (keepSelected is int position)
                SelectedQueueItem = Queue.FirstOrDefault(q => q.Position == position);

            OnPropertyChanged(nameof(QueueCountText));

            if (!_relinkChecked && Queue.Count > 0)
            {
                _relinkChecked = true;
                await RelinkQueueIfStaleAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not read the queue: {ex.Message}");
        }
    }

    /// <summary>
    /// A queue survives on the player between runs of this app, but the URLs in it name the
    /// address and port our media server had at the time. If this machine's IP changed, or the
    /// port moved, every entry is a dead link and the player just refuses to start. Rewrite them
    /// to this session's addresses.
    /// </summary>
    private async Task RelinkQueueIfStaleAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null || Queue.Count == 0) return;

        var repaired = new List<QueueSnapshotItem>(Queue.Count);
        var stale = 0;
        var unknown = 0;

        foreach (var item in Queue)
        {
            var current = _server.TryRebuildMediaUrl(item.Uri, coordinator.Ip);
            if (current is null)
            {
                // Not one of ours, or the file is no longer in the library — leave it alone.
                if (item.Uri.Contains("/media/", StringComparison.OrdinalIgnoreCase)) unknown++;
                repaired.Add(new QueueSnapshotItem(item.Uri, item.Title, item.Artist, item.Album,
                    item.Duration));
                continue;
            }

            if (!string.Equals(current, item.Uri, StringComparison.OrdinalIgnoreCase)) stale++;
            repaired.Add(new QueueSnapshotItem(current, item.Title, item.Artist, item.Album,
                item.Duration));
        }

        if (unknown > 0)
            AppendLog($"{unknown} queue entr(ies) point at files that are not in the library any more.");

        if (stale == 0) return;

        if (IsPlaying)
        {
            Status = $"{stale} queue entries point at an old address. Stop playback and press " +
                     "the queue Refresh to relink them.";
            return;
        }

        AppendLog($"Relinking {stale} queue entries to {coordinator.Host} via port {_server.Port}.");
        await RebuildQueueAsync(coordinator, repaired, resumePlaying: false);
        Status = $"Relinked {stale} queue entries to this session — they will play now.";
    }

    /// <summary>Inserts the selection straight after whatever is playing.</summary>
    [RelayCommand]
    private async Task PlayNextAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null) { Status = "Select a room first."; return; }

        var selection = EffectiveSelection;
        if (selection.Count == 0) { Status = "Pick one or more tracks first."; return; }

        var queueWasEmpty = Queue.Count == 0;

        IsBusy = true;
        try
        {
            // DesiredFirstTrackNumberEnqueued is 1-based; 0 means "append".
            var insertAt = _currentTrackNumber > 0 ? _currentTrackNumber + 1 : 0;

            foreach (var track in selection.Take(MaxQueueLength))
            {
                var url = _server.BuildFileUrl(track.Path, coordinator.Ip);
                await coordinator.AddUriToQueueAsync(url,
                    Didl.ForTrack(track.Title, track.Artist, track.Album, url, track.Duration),
                    insertAt,
                    asNext: true);

                if (insertAt > 0) insertAt++;
            }

            await LoadQueueAsync();

            // Adding to a queue nobody is playing should still make a sound.
            if (queueWasEmpty || !IsPlaying)
            {
                if (!await coordinator.IsPlayingFromQueueAsync())
                {
                    await coordinator.PlayFromQueueAsync();
                    await Task.Delay(250);
                }
                if (!IsPlaying) await coordinator.PlayWithRetryAsync();
            }

            Status = selection.Count == 1
                ? $"\"{selection[0].Title}\" plays next on {coordinator.RoomName}."
                : $"{selection.Count} tracks play next on {coordinator.RoomName}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not queue that: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    private void MarkNowPlaying(int trackNumber)
    {
        foreach (var item in Queue) item.IsNowPlaying = item.Position == trackNumber;
    }

    public async Task PlayQueueItemAsync(QueueItemViewModel item)
    {
        var coordinator = Coordinator;
        if (coordinator is null) return;

        try
        {
            if (!await coordinator.IsPlayingFromQueueAsync())
            {
                await coordinator.PlayFromQueueAsync();
                await Task.Delay(250);
            }

            await coordinator.SeekAsync("TRACK_NR", item.Position.ToString());
            await coordinator.PlayWithRetryAsync();
            MarkNowPlaying(item.Position);
        }
        catch (Exception ex)
        {
            Status = $"Could not jump to that track: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveFromQueueAsync()
    {
        var coordinator = Coordinator;
        var item = SelectedQueueItem;
        if (coordinator is null || item is null) return;

        var snapshot = SnapshotQueue();
        try
        {
            await coordinator.RemoveTrackFromQueueAsync(item.ObjectId);
            await LoadQueueAsync();
            PushUndo($"removing \"{item.Title}\"", snapshot);
        }
        catch (Exception ex)
        {
            Status = $"Could not remove that track: {ex.Message}";
        }
    }

    [RelayCommand] private Task MoveQueueItemUpAsync() => MoveQueueItemAsync(-1);
    [RelayCommand] private Task MoveQueueItemDownAsync() => MoveQueueItemAsync(1);

    private async Task MoveQueueItemAsync(int offset)
    {
        var coordinator = Coordinator;
        var item = SelectedQueueItem;
        if (coordinator is null || item is null) return;

        var target = item.Position + offset;
        if (target < 1 || target > Queue.Count) return;

        try
        {
            // ReorderTracksInQueue is 1-based, and InsertBefore counts positions in the queue
            // as it is *before* the move — so moving down needs one extra step.
            await coordinator.ReorderTracksInQueueAsync(item.Position, 1, offset > 0 ? target + 1 : target);
            await LoadQueueAsync();
            SelectedQueueItem = Queue.FirstOrDefault(q => q.Position == target);
        }
        catch (Exception ex)
        {
            Status = $"Could not reorder the queue: {ex.Message}";
        }
    }

    /// <summary>
    /// Genuinely reorders the queue rather than setting Sonos' shuffle play mode, so what the
    /// list shows is what will play. Undoable.
    ///
    /// The reordering happens on the player, entry by entry, instead of clearing the queue and
    /// adding everything back: a rebuild throws away the player's own metadata and loses every
    /// entry after the first one it cannot re-add. Moving entries touches neither, and playback
    /// carries on through it.
    /// </summary>
    [RelayCommand]
    private async Task ShuffleQueueAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null) return;

        // Work from what the player actually holds, not from whatever the list was showing.
        await LoadQueueAsync();
        var count = Queue.Count;
        if (count < 2) return;

        var snapshot = SnapshotQueue();

        IsBusy = true;
        try
        {
            // Fisher-Yates, played out as moves: each step picks one of the entries not yet
            // settled and sends it to the end of the unsettled run.
            for (var i = count; i > 1; i--)
            {
                var j = _random.Next(1, i + 1);   // 1-based position, inclusive of i
                if (j == i) continue;

                // Moving an entry to a higher position counts positions as they were *before*
                // the move, so the target is one past where it should land.
                await coordinator.ReorderTracksInQueueAsync(j, 1, i + 1);

                if ((count - i) % 25 == 24)
                    Status = $"Shuffling — {count - i + 1} of {count - 1} moves…";
            }

            await LoadQueueAsync();
            PushUndo("shuffling the queue", snapshot);
            Status = $"Queue shuffled ({count} tracks) — press Ctrl+Z to put it back.";
        }
        catch (Exception ex)
        {
            await LoadQueueAsync();
            Status = $"Shuffle failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ClearQueueAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null || Queue.Count == 0) return;

        var snapshot = SnapshotQueue();
        try
        {
            await coordinator.ClearQueueAsync();
            await LoadQueueAsync();
            PushUndo("clearing the queue", snapshot);
            Status = $"Queue cleared. {UndoDescription} with Ctrl+Z.";
        }
        catch (Exception ex)
        {
            Status = $"Could not clear the queue: {ex.Message}";
        }
    }

    private List<QueueSnapshotItem> SnapshotQueue() =>
        Queue.Select(q => new QueueSnapshotItem(q.Uri, q.Title, q.Artist, q.Album, q.Duration)).ToList();

    private async Task RebuildQueueAsync(SonosDevice coordinator, List<QueueSnapshotItem> items, bool resumePlaying)
    {
        var wanted = items.Count(i => !string.IsNullOrWhiteSpace(i.Uri));
        var added = 0;
        var failed = 0;

        await coordinator.ClearQueueAsync();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Uri)) continue;
            try
            {
                await coordinator.AddUriToQueueAsync(item.Uri,
                    Didl.ForTrack(item.Title, item.Artist, item.Album, item.Uri, item.Duration));
                added++;
            }
            catch (Exception ex)
            {
                // The queue has already been cleared at this point, so one refused entry must not
                // abandon the remaining hundreds — that is how a rebuild used to lose most of it.
                failed++;
                if (failed == 1) AppendLog($"Re-queuing \"{item.Title}\" failed: {ex.Message}");
            }
        }

        if (failed > 0)
            AppendLog($"Re-queued {added} of {wanted} entries; {failed} were refused by the player.");

        await LoadQueueAsync();

        if (!resumePlaying) return;

        await coordinator.PlayFromQueueAsync();
        await Task.Delay(250);
        await coordinator.PlayWithRetryAsync();
    }

    /// <summary>
    /// Raskin's rule: destructive actions get an undo, not a confirmation dialog.
    /// </summary>
    private void PushUndo(string description, List<QueueSnapshotItem> snapshot)
    {
        var coordinator = Coordinator;
        if (coordinator is null) return;

        _undo.Push(new UndoStep(description, async () =>
        {
            var wasPlaying = IsPlaying;
            await RebuildQueueAsync(coordinator, snapshot, wasPlaying);
        }));

        if (_undo.Count > 20)
        {
            var kept = _undo.Take(20).Reverse().ToList();
            _undo.Clear();
            foreach (var step in kept) _undo.Push(step);
        }

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoDescription));
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_undo.Count == 0) { Status = "Nothing to undo."; return; }

        var step = _undo.Pop();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoDescription));

        IsBusy = true;
        try
        {
            await step.Apply();
            Status = $"Undid {step.Description}.";
        }
        catch (Exception ex)
        {
            Status = $"Undo failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ================================================================ grouping

    [RelayCommand]
    private async Task GroupCheckedAsync()
    {
        if (SelectedRoom is null) { Status = "Select the room that should lead the group first."; return; }

        var leader = SelectedRoom;
        var members = Rooms.Where(r => r.IsChecked && r.Uuid != leader.Uuid).ToList();
        if (members.Count == 0) { Status = "Tick the rooms you want to join to the selected room."; return; }

        IsBusy = true;
        try
        {
            if (!leader.IsCoordinator) await leader.Device.LeaveGroupAsync();
            foreach (var member in members) await member.Device.JoinAsync(leader.Uuid);

            await Task.Delay(600);
            await LoadTopologyFromAnyAsync(new[] { leader.Device.Ip });
            Status = $"Grouped {members.Count} room(s) with {leader.RoomName}.";
        }
        catch (Exception ex)
        {
            Status = $"Grouping failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UngroupCheckedAsync()
    {
        var targets = Rooms.Where(r => r.IsChecked).ToList();
        if (targets.Count == 0 && SelectedRoom != null) targets.Add(SelectedRoom);
        if (targets.Count == 0) return;

        IsBusy = true;
        try
        {
            foreach (var room in targets) await room.Device.LeaveGroupAsync();
            await Task.Delay(600);
            await LoadTopologyFromAnyAsync(new[] { targets[0].Device.Ip });
            Status = "Rooms ungrouped.";
        }
        catch (Exception ex)
        {
            Status = $"Ungrouping failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    // ================================================================ streaming

    [RelayCommand]
    private void RefreshAudioSources()
    {
        var previousSource = SelectedAudioSource;
        var previousTargetId = SelectedRouteTarget?.Id ?? _settings.RouteDeviceId;

        // Rebuilding the lists reselects everything, which must not be mistaken for the user
        // choosing a different source while a stream is up.
        _suppressSourceSwitch = true;
        try
        {
            AudioSources.Clear();
            foreach (var option in AudioSourceCatalog.Build()) AudioSources.Add(option);

            SelectedAudioSource = AudioSources.FirstOrDefault(o => Matches(o, previousSource))
                                  ?? AudioSources.FirstOrDefault();

            RouteTargets.Clear();
            foreach (var device in AudioSourceCatalog.BuildRenderDevices()) RouteTargets.Add(device);

            SelectedRouteTarget = RouteTargets.FirstOrDefault(t => t.Id == previousTargetId)
                                  ?? RouteTargets.FirstOrDefault();
        }
        finally { _suppressSourceSwitch = false; }
    }

    private static bool Matches(AudioSourceOption option, AudioSourceOption? other)
    {
        if (other is null || option.Kind != other.Kind) return false;
        return option.Kind == AudioSourceKind.Device
            ? option.DeviceId == other.DeviceId
            : option.ProcessId == other.ProcessId;
    }

    [RelayCommand]
    private async Task ToggleDesktopStreamAsync()
    {
        if (IsDesktopStreaming) await StopDesktopStreamAsync();
        else await StartDesktopStreamAsync();
    }

    private async Task StartDesktopStreamAsync()
    {
        var coordinator = Coordinator;
        if (coordinator is null) { Status = "Select a room first."; return; }

        var choice = SelectedAudioSource;
        if (choice is null) { Status = "Pick something to stream first."; return; }

        IsBusy = true;
        try
        {
            _streamer.BitrateKbps = StreamBitrate;

            // Moving the application to another output first is the sturdier route: what comes
            // back is a plain endpoint capture, and the PC's own speakers stay quiet without
            // muting anything (muting the app would silence the capture too).
            var routed = await TryRouteSelectedAppAsync(choice);

            if (routed != null)
            {
                _streamer.Start(new DeviceLoopbackSource(
                    routed.Id, $"{choice.ProcessName} via \"{routed.Name}\""));
            }
            else
            {
                try
                {
                    _streamer.Start(CreateCaptureSource(choice));
                }
                catch (Exception ex) when (choice.Kind == AudioSourceKind.Process)
                {
                    AppendLog($"Per-application capture failed: {ex.Message}");
                    Status = $"Could not capture {choice.ProcessName} on its own — using whole-desktop audio.";
                    _streamer.Start(new DeviceLoopbackSource(null));
                }
            }

            // Sonos validates a radio URI by connecting and waiting for audio, so hand it a
            // stream that already has a backlog to burst rather than one starting from silence.
            // Only enough to prove the encoder is producing frames: waiting longer here is dead
            // time before the first note, and the burst-on-connect covers the rest.
            var primed = DateTime.UtcNow.AddSeconds(2);
            while (_streamer.Broadcaster.BufferedBytes < 12 * 1024 && DateTime.UtcNow < primed)
                await Task.Delay(50);

            AppendLog($"Encoder primed with {_streamer.Broadcaster.BufferedBytes / 1024} KB " +
                      $"({_streamer.EncodedBytes / 1024} KB encoded).");

            // Handing the player a URL that will never deliver audio just produces a confusing
            // timeout 45 seconds later, so stop here and say what actually went wrong.
            if (_streamer.Broadcaster.BufferedBytes == 0)
            {
                var reason = _streamer.LastError ?? "the capture produced no audio";
                _streamer.Stop();
                IsDesktopStreaming = false;
                Status = $"Nothing to stream — {reason}. See the activity log (⚙).";
                return;
            }

            var url = _server.BuildStreamUrl(coordinator.Ip);
            var title = _streamer.SourceDescription.Length > 0 ? _streamer.SourceDescription : "Desktop audio";

            await coordinator.SetAvTransportUriAsync(url, Didl.ForStream(title));

            // No pause here: PlayWithRetryAsync already handles the "transition not available"
            // fault that a too-early Play can raise, and a fixed delay is pure added latency.
            await coordinator.PlayWithRetryAsync();

            IsDesktopStreaming = true;

            Status = $"Streaming {_streamer.SourceDescription} to {coordinator.RoomName}.";
            AppendLog($"Stream URL: {url}");
        }
        catch (Exception ex)
        {
            _streamer.Stop();
            _router.RestoreAll();
            SavePendingRoutes();
            IsDesktopStreaming = false;
            Status = $"Could not start streaming: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Picking a different source while streaming changes it there and then. The encoder keeps
    /// running through the swap, so the players never see the connection drop.
    /// </summary>
    private void QueueSourceSwitch()
    {
        if (!IsDesktopStreaming || _suppressSourceSwitch) return;

        var previous = _switchCts;
        _switchCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        _ = SwitchStreamSourceAsync(_switchCts.Token);
    }

    private async Task SwitchStreamSourceAsync(CancellationToken ct)
    {
        // Arrow-keying down the dropdown would otherwise open and close a device per entry.
        try { await Task.Delay(400, ct); }
        catch (OperationCanceledException) { return; }

        var choice = SelectedAudioSource;
        if (choice is null || !IsDesktopStreaming || !_streamer.IsRunning) return;

        try
        {
            // The previous app goes back to its own output before the new one is moved.
            _router.RestoreAll();

            var routed = await TryRouteSelectedAppAsync(choice);
            if (ct.IsCancellationRequested) return;

            IAudioCaptureSource source = routed != null
                ? new DeviceLoopbackSource(routed.Id, $"{choice.ProcessName} via \"{routed.Name}\"")
                : CreateCaptureSource(choice);

            try
            {
                _streamer.SwitchSource(source);
            }
            catch (Exception ex) when (routed is null && choice.Kind == AudioSourceKind.Process)
            {
                AppendLog($"Per-application capture failed: {ex.Message}");
                _streamer.SwitchSource(new DeviceLoopbackSource(null));
            }

            SavePendingRoutes();
            Status = $"Streaming {_streamer.SourceDescription}" +
                     (Coordinator != null ? $" to {Coordinator.RoomName}." : ".");
        }
        catch (Exception ex)
        {
            AppendLog($"Could not switch the capture source: {ex.Message}");
            Status = $"Kept the previous source — {ex.Message}";
        }
    }

    /// <summary>
    /// Sends the chosen application to the chosen output device. Returns the device when it
    /// worked, so the caller knows to capture that endpoint instead of the application itself.
    /// </summary>
    private async Task<RenderDeviceOption?> TryRouteSelectedAppAsync(AudioSourceOption choice)
    {
        var target = SelectedRouteTarget;
        if (choice.Kind != AudioSourceKind.Process) return null;
        if (target is null || string.IsNullOrEmpty(target.Id)) return null;

        if (!AppAudioRouter.IsSupported)
        {
            AppendLog("This build of Windows cannot give an application its own output device.");
            return null;
        }

        if (!_router.Route(choice.ProcessId, target.Id)) return null;

        SavePendingRoutes();

        // Windows applies the change when the application next opens the device; give the ones
        // that switch on their own (browsers, most players) a moment to do it.
        await Task.Delay(700);
        return target;
    }

    /// <summary>Keeps the settings file in step so a crash cannot strand an app on a dead output.</summary>
    private void SavePendingRoutes()
    {
        _settings.PendingRoutes = _router.Pending;
        _settings.Save();
    }

    private static IAudioCaptureSource CreateCaptureSource(AudioSourceOption choice) =>
        choice.Kind == AudioSourceKind.Device
            ? new DeviceLoopbackSource(choice.DeviceId)
            : new ProcessLoopbackSource(choice.ProcessId, $"{choice.ProcessName} ({choice.Title})");

    private async Task StopDesktopStreamAsync()
    {
        _switchCts?.Cancel();

        var wasRouted = _router.IsActive;

        try
        {
            // Undone before anything else, so the PC has its sound back even if talking to the
            // player then fails or takes a while.
            _router.RestoreAll();

            // The user may have moved the app to that output by hand rather than letting the app
            // do it. Stopping should still hand the sound back either way.
            if (!wasRouted &&
                SelectedAudioSource is { Kind: AudioSourceKind.Process } app &&
                SelectedRouteTarget is { } target && target.Id.Length > 0)
            {
                var cleared = _router.ClearMatchingRoutes(app.ProcessId, target.Id);
                if (cleared > 0)
                {
                    wasRouted = true;
                    AppendLog($"Cleared the output override on {cleared} process(es) of " +
                              $"{app.ProcessName}, which was pointing at \"{target.Name}\".");
                }
            }

            SavePendingRoutes();

            var coordinator = Coordinator;
            if (coordinator != null)
            {
                try { await coordinator.StopAsync(); } catch { }
            }
            _streamer.Stop();
        }
        finally
        {
            IsDesktopStreaming = false;
            Status = wasRouted
                ? "Streaming stopped — the app is back on this PC's normal output."
                : "Streaming stopped.";
        }
    }

    // ================================================================ tick

    /// <summary>
    /// Events carry state changes; this only polls the playback position (which no event
    /// reports) plus an occasional full reconcile in case the callbacks are being blocked.
    /// </summary>
    private async Task TickAsync()
    {
        if (_tickBusy) return;
        var coordinator = Coordinator;
        if (coordinator is null) return;

        _tickBusy = true;
        _tickCount++;
        try
        {
            var fullSync = _tickCount % 10 == 0 || !IsLiveEventing;

            if (IsPlaying || fullSync)
            {
                var position = await coordinator.GetPositionInfoAsync();

                var elapsed = ParseTime(position.GetValueOrDefault("RelTime"));
                var total = ParseTime(position.GetValueOrDefault("TrackDuration"));

                DurationSeconds = total.TotalSeconds;
                DurationText = total > TimeSpan.Zero ? Format(total) : "live";
                if (!_userIsSeeking) PositionSeconds = elapsed.TotalSeconds;
                PositionText = Format(elapsed);

                var title = Didl.TitleFrom(position.GetValueOrDefault("TrackMetaData"));
                if (!string.IsNullOrWhiteSpace(title)) NowPlaying = title!;
                else if (TransportState == "STOPPED") NowPlaying = "Nothing playing";

                NowPlayingDetail = coordinator.RoomName;
            }

            if (fullSync)
            {
                TransportState = await coordinator.GetTransportStateAsync();

                if (SelectedRoom != null)
                {
                    SelectedRoom.SetVolumeFromDevice(await SelectedRoom.Device.GetVolumeAsync());
                    OnPropertyChanged(nameof(RoomVolume));
                }

                var media = await coordinator.GetMediaInfoAsync();
                var currentUri = media.GetValueOrDefault("CurrentURI") ?? "";
                var loadedCount = int.TryParse(media.GetValueOrDefault("NrTracks"), out var n) ? n : 0;

                if (currentUri.StartsWith("x-rincon-queue:", StringComparison.OrdinalIgnoreCase) &&
                    loadedCount != Queue.Count)
                    await LoadQueueAsync();
            }

            if (IsDesktopStreaming)
            {
                CaptureLevelText = _streamer.CapturedBytes == 0
                    ? "no audio from that device"
                    : _streamer.LevelText;

                // New browser tabs and helper processes appear mid-stream; they have to be sent
                // to the same output or half the sound stays on the PC.
                if (_router.IsActive && _tickCount % 3 == 0)
                {
                    var before = _router.Pending.Count;
                    _router.Reapply();
                    if (_router.Pending.Count != before) SavePendingRoutes();
                }
            }
            else
            {
                CaptureLevelText = "";
            }
        }
        catch
        {
            // Transient network errors during polling are not worth surfacing.
        }
        finally { _tickBusy = false; }
    }

    private static TimeSpan ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "NOT_IMPLEMENTED") return TimeSpan.Zero;
        return TimeSpan.TryParse(value, out var parsed) ? parsed : TimeSpan.Zero;
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private void AppendLog(string message)
    {
        var app = System.Windows.Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => AppendLog(message));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrEmpty(LogText) ? line : $"{LogText}\n{line}";

        var lines = LogText.Split('\n');
        if (lines.Length > 300) LogText = string.Join('\n', lines.Skip(lines.Length - 300));
    }

    public void PersistWindowSize(double width, double height)
    {
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _settings.Save();
    }

    public (double Width, double Height) RestoreWindowSize() => (_settings.WindowWidth, _settings.WindowHeight);

    public (double Rooms, double Library) RestoreColumnWidths() =>
        (_settings.RoomsWidth, _settings.LibraryWidth);

    public bool MinimizeToTray => _settings.MinimizeToTray;

    public double CompactWidth
    {
        get => _settings.CompactWidth;
        set
        {
            if (value < 340) return;
            _settings.CompactWidth = value;
            _settings.Save();
        }
    }

    /// <summary>
    /// Written on the way out, so what is on screen at exit is what comes back — collapsed panels
    /// included. The collapse toggles also save as they happen; this is the authoritative pass.
    /// </summary>
    public void PersistLayout(double rooms, double library)
    {
        if (rooms > 60) _settings.RoomsWidth = rooms;
        if (library > 150) _settings.LibraryWidth = library;
        _settings.RoomsExpanded = IsRoomsExpanded;
        _settings.LibraryExpanded = IsLibraryExpanded;
        _settings.Save();
    }

    /// <summary>Last line of defence: the routing must not outlive the process.</summary>
    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            _router.RestoreAll();
            _settings.PendingRoutes = _router.Pending;
            _settings.Save();
        }
        catch
        {
            // Nothing useful can be done this late; the next start repairs it from settings.json.
        }
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        _tick.Stop();
        _scanCts?.Cancel();
        _switchCts?.Cancel();

        // Undo routing first: leaving it in place would send an application's sound to an output
        // the user cannot hear, and it survives a reboot.
        _router.Dispose();

        _settings.StreamBitrate = StreamBitrate;
        _settings.LibraryFolders = LibraryFolders.ToList();
        _settings.PendingRoutes = _router.Pending;
        _settings.Save();

        _gena.Dispose();
        _streamer.Dispose();
        _server.Dispose();
    }
}
