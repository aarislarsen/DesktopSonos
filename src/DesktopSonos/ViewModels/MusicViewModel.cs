using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DesktopSonos.Music;
using DesktopSonos.Persistence;
using DesktopSonos.Sonos;
using DesktopSonos.Spotify;

namespace DesktopSonos.ViewModels;

/// <summary>Which list the panel is showing.</summary>
public enum MusicSource
{
    Search,
    MyPlaylists,
    SavedAlbums,
    LikedSongs,

    /// <summary>Saved queues on the players — the playlists this app can create.</summary>
    SonosPlaylists,

    /// <summary>Everything favourited in the Sonos app. Needs no Spotify sign-in of ours.</summary>
    Favourites,

    /// <summary>A scratch list of Spotify links that have been pasted in.</summary>
    Links
}

/// <summary>
/// The Spotify tab. Finding music goes through the Spotify Web API with the user's own login;
/// playing it does not — the queue verbs hand the player a Spotify service URI and the player
/// streams it directly, exactly as the Sonos app does. That is why the household must have
/// Spotify linked in the Sonos app before anything here makes a sound.
/// </summary>
public partial class MusicViewModel : ObservableObject
{
    /// <summary>The same 500-entry ceiling the rest of the app works to.</summary>
    private const int MaxQueueLength = 500;

    private readonly SpotifyAuth _auth = new();
    private readonly SpotifyApi _api;
    private readonly Func<SonosDevice?> _coordinator;
    private readonly Func<Task> _reloadQueue;
    private readonly Func<int> _queueLength;
    private readonly Func<int> _currentTrackNumber;
    private readonly Action<string> _setStatus;
    private readonly Action<string> _log;

    private AppSettings _settings = new();
    private bool _settingsReady;

    /// <summary>Resolved once per household, then reused — discovery is two Browse calls.</summary>
    private SpotifyAccount? _account;

    private CancellationTokenSource? _searchCts;
    private List<MusicItem> _selectedItems = new();

    /// <summary>sid to service name, read from a player once and reused.</summary>
    private Dictionary<int, string> _services = new();

    /// <summary>
    /// What the current list holds before the service filter narrows it, so switching the filter
    /// costs nothing and never has to re-read anything.
    /// </summary>
    private List<MusicItem> _loaded = new();

    /// <summary>What was on screen before opening an album or playlist, so Back can restore it.</summary>
    private List<MusicItem>? _listBeforeOpen;
    private string _headingBeforeOpen = "";

    public MusicViewModel(Func<SonosDevice?> coordinator, Func<Task> reloadQueue,
        Func<int> queueLength, Func<int> currentTrackNumber,
        Action<string> setStatus, Action<string> log)
    {
        _coordinator = coordinator;
        _reloadQueue = reloadQueue;
        _queueLength = queueLength;
        _currentTrackNumber = currentTrackNumber;
        _setStatus = setStatus;
        _log = log;

        _api = new SpotifyApi(_auth);
        _auth.Log += log;
        _api.Log += log;
    }

    // ================================================================ state

    public ObservableCollection<MusicItem> Results { get; } = new();

    /// <summary>
    /// The services actually present in the list on screen, so a household with Spotify and
    /// YouTube Music can show one at a time. Built from the content, not from a fixed list.
    /// </summary>
    public ObservableCollection<string> Services { get; } = new() { AllServices };

    public const string AllServices = "All services";

    [ObservableProperty] private string serviceFilter = AllServices;

    partial void OnServiceFilterChanged(string value) => ApplyServiceFilter();

    /// <summary>More than one service in the list is the only case where the filter is any use.</summary>
    public bool HasServiceChoice => Services.Count > 2;

    [ObservableProperty] private MusicItem? selectedItem;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string heading = "Search Spotify";
    [ObservableProperty] private string resultSummary = "";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string accountName = "";
    [ObservableProperty] private string newPlaylistName = "";

    /// <summary>A pasted Spotify link or URI, which plays without any sign-in.</summary>
    [ObservableProperty] private string linkText = "";

    /// <summary>
    /// Favourites, not search, is the opening list: it is the one source that works with nothing
    /// set up, so a first run has something in it rather than an empty box and a sign-in prompt.
    /// </summary>
    [ObservableProperty] private MusicSource source = MusicSource.Favourites;

    /// <summary>Search kinds, so "abbey road" can be narrowed to albums when that is what is wanted.</summary>
    [ObservableProperty] private bool searchTracks = true;
    [ObservableProperty] private bool searchAlbums = true;
    [ObservableProperty] private bool searchPlaylists = true;

    /// <summary>Pasted from the user's Spotify developer dashboard; there is no shared id.</summary>
    [ObservableProperty] private string clientId = "";

    [ObservableProperty] private int redirectPort = 8098;

    /// <summary>What has to be registered as a redirect URI on the Spotify application.</summary>
    public string RedirectUri => $"http://127.0.0.1:{RedirectPort}/callback";

    /// <summary>True while an album or playlist is open, which is when Back means something.</summary>
    public bool CanGoBack => _listBeforeOpen is not null;

    public bool IsSearchSource => Source == MusicSource.Search;
    public bool IsSonosPlaylistSource => Source == MusicSource.SonosPlaylists;

    /// <summary>Sources that need a Spotify sign-in of our own; the rest work with none.</summary>
    public bool SourceNeedsSignIn => Source is MusicSource.Search or MusicSource.MyPlaylists
        or MusicSource.SavedAlbums or MusicSource.LikedSongs;

    public string SelectionSummary => _selectedItems.Count > 1 ? $"{_selectedItems.Count} selected" : "";

    public string ConnectionSummary => IsConnected
        ? string.IsNullOrEmpty(AccountName) ? "Connected" : $"Connected as {AccountName}"
        : "Not connected";

    /// <summary>Named so the settings drawer can say which account the players will stream from.</summary>
    public string HouseholdAccountSummary => _account is null
        ? "Spotify service on the players: not detected yet"
        : $"Spotify service on the players: sid {_account.Sid}, account {_account.Sn}";

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(ConnectionSummary));
    partial void OnAccountNameChanged(string value) => OnPropertyChanged(nameof(ConnectionSummary));

    partial void OnRedirectPortChanged(int value)
    {
        _auth.RedirectPort = value;
        OnPropertyChanged(nameof(RedirectUri));
        if (!_settingsReady) return;
        _settings.SpotifyRedirectPort = value;
        _settings.Save();
    }

    partial void OnClientIdChanged(string value)
    {
        _auth.ClientId = value?.Trim() ?? "";
        if (!_settingsReady) return;
        _settings.SpotifyClientId = _auth.ClientId;
        _settings.Save();
    }

    partial void OnSourceChanged(MusicSource value)
    {
        OnPropertyChanged(nameof(IsSearchSource));
        OnPropertyChanged(nameof(IsSonosPlaylistSource));
        OnPropertyChanged(nameof(SourceNeedsSignIn));
    }

    partial void OnSearchTextChanged(string value) => QueueSearch();

    // Narrowing a search to albums should show albums straight away, not on the next keystroke.
    partial void OnSearchTracksChanged(bool value) => QueueSearch();
    partial void OnSearchAlbumsChanged(bool value) => QueueSearch();
    partial void OnSearchPlaylistsChanged(bool value) => QueueSearch();

    public void SetSelectedItems(IEnumerable<MusicItem> items)
    {
        _selectedItems = items.ToList();
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private List<MusicItem> EffectiveSelection =>
        _selectedItems.Count > 0
            ? _selectedItems
            : SelectedItem is null ? new List<MusicItem>() : new List<MusicItem> { SelectedItem };

    // ================================================================ startup

    /// <summary>
    /// Called once the settings file has been read. Restores the link silently: a refresh token
    /// on disk means the account is already connected and nothing has to be clicked.
    /// </summary>
    public void Start(AppSettings settings)
    {
        _settings = settings;

        // Assigning the observable properties writes back to settings, so the flag goes up after.
        ClientId = settings.SpotifyClientId;
        RedirectPort = settings.SpotifyRedirectPort > 0 ? settings.SpotifyRedirectPort : 8098;
        _settingsReady = true;

        _auth.ClientId = ClientId;
        _auth.RedirectPort = RedirectPort;

        if (settings.SpotifySid > 0)
        {
            _account = string.IsNullOrWhiteSpace(settings.SpotifyCdudn)
                ? SpotifyAccount.FromSid(settings.SpotifySid, Math.Max(settings.SpotifySn, 1))
                : new SpotifyAccount(settings.SpotifySid, Math.Max(settings.SpotifySn, 1),
                    settings.SpotifyCdudn);
            OnPropertyChanged(nameof(HouseholdAccountSummary));
        }

        IsConnected = _auth.IsConnected;
        AccountName = _auth.DisplayName;

        if (IsConnected) _ = ConfirmAccountAsync();
    }

    /// <summary>Verifies the stored link in the background and picks up a changed display name.</summary>
    private async Task ConfirmAccountAsync()
    {
        try
        {
            var me = await _api.GetMeAsync().ConfigureAwait(true);
            if (me is null)
            {
                IsConnected = _auth.IsConnected;
                return;
            }

            AccountName = me.Value.Name;
            _auth.RememberAccount(me.Value.Name, me.Value.Id);
        }
        catch (Exception ex)
        {
            _log($"Could not confirm the Spotify account: {ex.Message}");
        }
    }

    // ================================================================ connection

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            _setStatus("Paste a Spotify client id into the Spotify settings first.");
            return;
        }

        IsBusy = true;
        try
        {
            _setStatus("Waiting for the Spotify sign-in in your browser…");
            await _auth.ConnectAsync();

            IsConnected = true;
            await ConfirmAccountAsync();

            _setStatus($"Spotify connected{(string.IsNullOrEmpty(AccountName) ? "" : $" as {AccountName}")}.");
            await LoadSourceAsync();
        }
        catch (Exception ex)
        {
            IsConnected = _auth.IsConnected;
            _setStatus($"Spotify sign-in failed: {ex.Message}");
            _log($"Spotify sign-in failed: {ex}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _auth.Disconnect();
        IsConnected = false;
        AccountName = "";
        Results.Clear();
        ResultSummary = "";
        _setStatus("Spotify disconnected. The stored sign-in has been deleted.");
    }

    // ================================================================ browsing

    [RelayCommand] private Task ShowFavourites() => SwitchSourceAsync(MusicSource.Favourites);
    [RelayCommand] private Task ShowSearch() => SwitchSourceAsync(MusicSource.Search);
    [RelayCommand] private Task ShowMyPlaylists() => SwitchSourceAsync(MusicSource.MyPlaylists);
    [RelayCommand] private Task ShowSavedAlbums() => SwitchSourceAsync(MusicSource.SavedAlbums);
    [RelayCommand] private Task ShowLikedSongs() => SwitchSourceAsync(MusicSource.LikedSongs);
    [RelayCommand] private Task ShowSonosPlaylists() => SwitchSourceAsync(MusicSource.SonosPlaylists);

    private Task SwitchSourceAsync(MusicSource value)
    {
        Source = value;
        ClearDrillDown();
        return LoadSourceAsync();
    }

    [RelayCommand]
    private Task Refresh() => LoadSourceAsync();

    private async Task LoadSourceAsync()
    {
        // Favourites, saved queues and pasted links come from the players, so they work with no
        // Spotify sign-in of ours at all. Only the account's own lists need one.
        if (!IsConnected && SourceNeedsSignIn)
        {
            Results.Clear();
            Heading = "Sign in to search Spotify";
            ResultSummary = "";
            return;
        }

        IsBusy = true;
        try
        {
            switch (Source)
            {
                case MusicSource.Search:
                    Heading = "Search Spotify";
                    await RunSearchAsync(CancellationToken.None);
                    return;

                case MusicSource.MyPlaylists:
                    Heading = "Your playlists";
                    Show(await _api.GetMyPlaylistsAsync());
                    return;

                case MusicSource.SavedAlbums:
                    Heading = "Saved albums";
                    Show(await _api.GetSavedAlbumsAsync());
                    return;

                case MusicSource.LikedSongs:
                    Heading = "Liked songs";
                    Show(await _api.GetLikedTracksAsync());
                    return;

                case MusicSource.SonosPlaylists:
                    Heading = "Sonos playlists";
                    Show(await LoadSonosPlaylistsAsync());
                    return;

                case MusicSource.Favourites:
                    Heading = "Sonos favourites";
                    Show(await LoadFavouritesAsync());
                    return;

                case MusicSource.Links:
                    // A scratch list; re-reading it would mean throwing away what was pasted.
                    Heading = "Pasted links";
                    return;
            }
        }
        catch (Exception ex)
        {
            _setStatus($"Spotify: {ex.Message}");
            _log($"Spotify request failed: {ex}");
        }
        finally { IsBusy = false; }
    }

    private async Task<List<MusicItem>> LoadSonosPlaylistsAsync()
    {
        var coordinator = _coordinator();
        if (coordinator is null)
        {
            _setStatus("Select a room first — saved playlists are read from the player.");
            return new List<MusicItem>();
        }

        var playlists = await SonosPlaylists.ListAsync(coordinator);
        return playlists.Select(p => new MusicItem
        {
            Kind = MusicItemKind.SonosPlaylist,
            Id = p.ObjectId,
            Title = p.Title,
            Subtitle = "Sonos playlist",
            Uri = p.Uri
        }).ToList();
    }

    /// <summary>
    /// Favourites are the one music-service source that needs nothing set up: each entry already
    /// carries the URI and the signed metadata for whichever account the household is linked to,
    /// so playing one is exactly the call the Sonos app makes.
    /// </summary>
    private async Task<List<MusicItem>> LoadFavouritesAsync()
    {
        var coordinator = _coordinator();
        if (coordinator is null)
        {
            _setStatus("Select a room first — favourites are read from the player.");
            return new List<MusicItem>();
        }

        var favourites = SonosFavorites.WithServiceNames(
            await SonosFavorites.ListAsync(coordinator),
            await EnsureServiceNamesAsync(coordinator));

        return favourites.Select(f => new MusicItem
        {
            Kind = MusicItemKind.Favorite,
            Id = f.Uri,
            Title = f.Title,
            Subtitle = f.Description,
            Service = f.Service,
            Uri = f.Uri,
            Metadata = f.Metadata,
            IsStream = f.IsStream
        }).ToList();
    }

    /// <summary>
    /// The sid-to-name map, read once. Names are decoration, so a failure here leaves the rows
    /// unlabelled rather than emptying the list.
    /// </summary>
    private async Task<Dictionary<int, string>> EnsureServiceNamesAsync(SonosDevice coordinator)
    {
        if (_services.Count > 0) return _services;
        _services = await SonosMusicServices.ListAsync(coordinator);
        return _services;
    }

    /// <summary>
    /// Adds a pasted Spotify link to the list, where the ordinary verbs then apply. No account is
    /// involved: the id is in the link, and the players resolve it against their own Spotify
    /// account. The title only becomes the real one once the player has it in the queue.
    /// </summary>
    [RelayCommand]
    private void AddLink()
    {
        var item = SpotifyLink.Parse(LinkText);
        if (item is null)
        {
            _setStatus("That is not a Spotify link. Use Share ▸ Copy link, or Copy Spotify URI.");
            return;
        }

        if (Source != MusicSource.Links)
        {
            Source = MusicSource.Links;
            ClearDrillDown();
            Results.Clear();
            Heading = "Pasted links";
        }

        Results.Insert(0, item);
        SelectedItem = item;
        SetSelectedItems(new[] { item });
        LinkText = "";
        ResultSummary = $"{Results.Count} pasted";
        _setStatus($"Added the {item.KindLabel} from the link — now press Play now or Add to queue.");
    }

    private void Show(List<MusicItem> items)
    {
        _loaded = items;

        // Rebuilt from what is actually in the list: a household that has never linked YouTube
        // Music should not be offered it as a filter.
        var present = items.Select(i => i.Service)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Services.Clear();
        Services.Add(AllServices);
        foreach (var service in present) Services.Add(service);
        OnPropertyChanged(nameof(HasServiceChoice));

        // Keep the current filter if it still applies, so paging around does not reset it.
        if (!Services.Contains(ServiceFilter, StringComparer.OrdinalIgnoreCase))
            ServiceFilter = AllServices;

        ApplyServiceFilter();
    }

    private void ApplyServiceFilter()
    {
        var filtered = ServiceFilter == AllServices || string.IsNullOrEmpty(ServiceFilter)
            ? _loaded
            : _loaded.Where(i => string.Equals(i.Service, ServiceFilter,
                StringComparison.OrdinalIgnoreCase)).ToList();

        Results.Clear();
        foreach (var item in filtered) Results.Add(item);
        SetSelectedItems(Array.Empty<MusicItem>());

        ResultSummary = filtered.Count == 0
            ? "nothing found"
            : filtered.Count == _loaded.Count
                ? $"{filtered.Count} result(s)"
                : $"{filtered.Count} of {_loaded.Count}";
    }

    /// <summary>
    /// Typing runs the search on a short delay, so a search is not fired per keystroke. The
    /// previous one is cancelled rather than left to finish and overwrite newer results.
    /// </summary>
    private void QueueSearch()
    {
        if (Source != MusicSource.Search || !IsConnected) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = DebounceSearchAsync(_searchCts.Token);
    }

    /// <summary>
    /// Started from a property change, which happens on the UI thread, so the delay's
    /// continuation comes back to it and the results collection is only ever touched there.
    /// </summary>
    private async Task DebounceSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(450, ct);
            await RunSearchAsync(ct);
        }
        catch (OperationCanceledException) { /* superseded by a newer keystroke */ }
    }

    [RelayCommand]
    private Task SearchNow()
    {
        _searchCts?.Cancel();
        ClearDrillDown();
        return RunSearchAsync(CancellationToken.None);
    }

    private async Task RunSearchAsync(CancellationToken ct)
    {
        if (!IsConnected) return;

        var query = SearchText?.Trim() ?? "";
        if (query.Length == 0)
        {
            Results.Clear();
            ResultSummary = "";
            return;
        }

        try
        {
            var results = await _api.SearchAsync(query, SearchTracks, SearchAlbums, SearchPlaylists,
                ct: ct);
            if (ct.IsCancellationRequested) return;

            ClearDrillDown();
            Heading = $"Search — “{query}”";
            Show(results);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            _setStatus($"Spotify search failed: {ex.Message}");
        }
    }

    /// <summary>Opens an album or playlist to show its tracks. Double-click and Enter do this.</summary>
    public async Task OpenAsync(MusicItem item)
    {
        if (!item.CanOpen) return;

        IsBusy = true;
        try
        {
            var tracks = item.Kind switch
            {
                MusicItemKind.Album => await _api.GetAlbumTracksAsync(item.Id, item.Title),
                MusicItemKind.Playlist => await _api.GetPlaylistTracksAsync(item.Id),
                MusicItemKind.Artist => await _api.GetArtistTopTracksAsync(item.Id),
                MusicItemKind.SonosPlaylist => await ReadSavedQueueAsync(item),
                _ => new List<MusicItem>()
            };

            // Remembered before the list is replaced, so Back is exact rather than a re-search.
            if (_listBeforeOpen is null)
            {
                _listBeforeOpen = Results.ToList();
                _headingBeforeOpen = Heading;
                OnPropertyChanged(nameof(CanGoBack));
            }

            Heading = item.Title;
            Show(tracks);
        }
        catch (Exception ex)
        {
            _setStatus($"Could not open “{item.Title}”: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Reads the tracks inside a saved queue. Each keeps its own URI and metadata, so a single
    /// YouTube Music track can be queued again even though its id is an opaque service token
    /// that nothing outside the household could construct.
    /// </summary>
    private async Task<List<MusicItem>> ReadSavedQueueAsync(MusicItem playlist)
    {
        var coordinator = _coordinator();
        if (coordinator is null)
        {
            _setStatus("Select a room first.");
            return new List<MusicItem>();
        }

        var services = await EnsureServiceNamesAsync(coordinator);
        var entries = await SonosPlaylists.ReadEntriesAsync(coordinator, playlist.Id);

        return entries.Select(e => new MusicItem
        {
            // Favourite is the generic "already playable" row: a URI plus signed metadata, with
            // nothing about the service needing to be understood here.
            Kind = MusicItemKind.Favorite,
            Id = e.Uri,
            Title = e.Title,
            Subtitle = e.Artist,
            Detail = e.Album,
            Duration = e.Duration,
            Service = SonosMusicServices.NameFor(e.Uri, services),
            Uri = e.Uri,
            Metadata = e.Metadata
        }).ToList();
    }

    [RelayCommand]
    private void Back()
    {
        if (_listBeforeOpen is null) return;

        var restored = _listBeforeOpen;
        var heading = _headingBeforeOpen;
        ClearDrillDown();

        Heading = heading;
        Show(restored);
    }

    private void ClearDrillDown()
    {
        if (_listBeforeOpen is null) return;
        _listBeforeOpen = null;
        _headingBeforeOpen = "";
        OnPropertyChanged(nameof(CanGoBack));
    }

    // ================================================================ queueing

    /// <summary>
    /// Works out which Spotify service and account the household is linked to. Discovery reads it
    /// back off existing Spotify content on the players; the fallback is what Sonos has used for
    /// Spotify for years, and is only reached when the household has none.
    /// </summary>
    private async Task<SpotifyAccount> EnsureAccountAsync(SonosDevice coordinator)
    {
        if (_account is not null) return _account;

        _account = await SonosSpotify.DiscoverAsync(coordinator);

        if (_account is null)
        {
            _account = SpotifyAccount.Fallback;
            _log("No Spotify content found on the household, so the Spotify service id could not " +
                 "be confirmed. Falling back to sid 9 / account 1. If nothing plays, link Spotify " +
                 "in the Sonos app first.");
        }
        else
        {
            _log($"Household Spotify service: sid {_account.Sid}, account {_account.Sn}.");
        }

        // Remembered so later sessions skip discovery, and so the values can be corrected by hand.
        _settings.SpotifySid = _account.Sid;
        _settings.SpotifySn = _account.Sn;
        _settings.SpotifyCdudn = _account.Cdudn;
        _settings.Save();

        OnPropertyChanged(nameof(HouseholdAccountSummary));
        return _account;
    }

    /// <summary>
    /// Hands one item to the player. A track becomes a single queue entry; an album or playlist
    /// is enqueued as a container and the player expands it, so a 60-track playlist is one SOAP
    /// call rather than sixty.
    /// </summary>
    private Task<int> EnqueueAsync(SonosDevice coordinator, SpotifyAccount? account, MusicItem item,
        int insertAt, bool asNext)
    {
        // Only id-based Spotify items need the household's service numbers; favourites and saved
        // queues carry their own, so the account is resolved lazily and may legitimately be null.
        account ??= SpotifyAccount.Fallback;

        var (uri, metadata) = item.Kind switch
        {
            MusicItemKind.Track => (
                SonosSpotify.TrackUri(account, item.Id),
                SonosSpotify.TrackMetadata(account, item.Id, item.Title, item.Subtitle, item.Detail)),

            MusicItemKind.Album => (
                SonosSpotify.AlbumUri(account, item.Id),
                SonosSpotify.AlbumMetadata(account, item.Id, item.Title, item.Subtitle)),

            MusicItemKind.Playlist => (
                SonosSpotify.PlaylistUri(account, item.Id),
                SonosSpotify.PlaylistMetadata(account, item.Id, item.Title)),

            MusicItemKind.Artist => (
                SonosSpotify.ArtistUri(account, item.Id),
                SonosSpotify.ArtistMetadata(account, item.Id, item.Title)),

            // A favourite already carries both, signed for the household's own account.
            MusicItemKind.Favorite => (item.Uri, item.Metadata),

            _ => (item.Uri, SonosPlaylists.Metadata(new SonosPlaylist
            {
                ObjectId = item.Id,
                Title = item.Title,
                Uri = item.Uri
            }))
        };

        return coordinator.AddUriToQueueAsync(uri, metadata, insertAt, asNext);
    }

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        var selection = EffectiveSelection;
        if (selection.Count == 0) { _setStatus("Pick something first."); return; }
        await EnqueueManyAsync(selection, insertNext: false, replace: false, "Added");
    }

    [RelayCommand]
    private async Task PlayNextAsync()
    {
        var selection = EffectiveSelection;
        if (selection.Count == 0) { _setStatus("Pick something first."); return; }
        await EnqueueManyAsync(selection, insertNext: true, replace: false, "Playing next");
    }

    [RelayCommand]
    private async Task PlayNowAsync()
    {
        var selection = EffectiveSelection;
        if (selection.Count == 0) { _setStatus("Pick something first."); return; }
        await EnqueueManyAsync(selection, insertNext: false, replace: true, "Playing");
    }

    /// <summary>Replaces the queue with everything currently listed.</summary>
    [RelayCommand]
    private async Task QueueAllAsync()
    {
        if (Results.Count == 0) { _setStatus("Nothing listed to queue."); return; }
        await EnqueueManyAsync(Results.ToList(), insertNext: false, replace: true, "Playing");
    }

    /// <summary>
    /// Fills the queue with tracks drawn at random from what is listed. Containers are skipped:
    /// a random album would land as a block, which is not what shuffling means.
    /// </summary>
    [RelayCommand]
    private async Task ShuffleInAsync()
    {
        // Single playable rows only: a container would land as a block, which is not what
        // shuffling means, and a station cannot go in a queue at all.
        var pool = Results
            .Where(r => r.Kind is MusicItemKind.Track or MusicItemKind.Favorite && !r.IsStream)
            .ToList();

        if (pool.Count == 0)
        {
            _setStatus("No tracks listed to shuffle in — open an album or playlist first.");
            return;
        }

        var room = MaxQueueLength - _queueLength();
        if (room <= 0)
        {
            _setStatus($"The queue is already at the {MaxQueueLength}-track limit — clear it first.");
            return;
        }

        // Partial Fisher-Yates: as many draws as there are slots, and no track drawn twice.
        var take = Math.Min(room, pool.Count);
        for (var i = 0; i < take; i++)
        {
            var j = Random.Shared.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        await EnqueueManyAsync(pool.Take(take).ToList(), insertNext: false, replace: false, "Added");
    }

    private async Task EnqueueManyAsync(List<MusicItem> items, bool insertNext, bool replace,
        string verb)
    {
        var coordinator = _coordinator();
        if (coordinator is null) { _setStatus("Select a room first."); return; }

        IsBusy = true;
        try
        {
            // A station is not queueable at all — the player takes it as the transport URI — so
            // "play now" on one is a different call, and queueing one is refused rather than
            // silently dropped.
            if (replace && items.Count == 1 && items[0].IsStream)
            {
                await coordinator.SetAvTransportUriAsync(items[0].Uri, items[0].Metadata);
                await coordinator.PlayWithRetryAsync();
                await _reloadQueue();
                _setStatus($"Playing “{items[0].Title}” on {coordinator.RoomName}.");
                return;
            }

            var streams = items.Count(i => i.IsStream);
            if (streams > 0)
            {
                items = items.Where(i => !i.IsStream).ToList();
                _log($"Skipped {streams} station(s): Sonos cannot put a live stream in a queue. " +
                     "Use Play now on a single station instead.");
                if (items.Count == 0)
                {
                    _setStatus("A station cannot go in the queue — pick just it and press Play now.");
                    return;
                }
            }

            // Resolved only when something actually needs it: favourites and saved queues carry
            // their own service token, so browsing them never triggers discovery.
            var account = items.Any(i => i.Kind is MusicItemKind.Track or MusicItemKind.Album
                              or MusicItemKind.Playlist or MusicItemKind.Artist)
                ? await EnsureAccountAsync(coordinator)
                : null;

            if (replace) await coordinator.ClearQueueAsync();

            var queueWasEmpty = replace || _queueLength() == 0;

            // DesiredFirstTrackNumberEnqueued is 1-based; 0 means append. A container expands into
            // several entries, so the insert point moves by however many the player actually added
            // rather than by one per item.
            var length = replace ? 0 : await coordinator.GetLoadedTrackCountAsync();
            var insertAt = insertNext && !replace && _currentTrackNumber() > 0
                ? _currentTrackNumber() + 1
                : 0;

            var added = 0;
            foreach (var item in items)
            {
                if (length >= MaxQueueLength)
                {
                    _log($"Stopped at the {MaxQueueLength}-entry Sonos queue limit.");
                    break;
                }

                var newLength = await EnqueueAsync(coordinator, account, item, insertAt,
                    insertNext && insertAt > 0);

                if (newLength > length)
                {
                    if (insertAt > 0) insertAt += newLength - length;
                    length = newLength;
                }
                else
                {
                    // Some firmware answers 0 rather than the new length; keep going, and fall
                    // back to appending so entries cannot pile up in reverse order.
                    length++;
                    insertAt = 0;
                }

                added++;
            }

            if (replace)
            {
                await coordinator.PlayFromQueueAsync();
                await Task.Delay(250);
                await coordinator.PlayWithRetryAsync();
            }
            else if (insertNext && queueWasEmpty)
            {
                // Queueing into a queue nobody is playing should still make a sound.
                if (!await coordinator.IsPlayingFromQueueAsync())
                {
                    await coordinator.PlayFromQueueAsync();
                    await Task.Delay(250);
                }
                await coordinator.PlayWithRetryAsync();
            }

            await _reloadQueue();

            _setStatus(added == 1
                ? $"{verb} “{items[0].Title}” on {coordinator.RoomName}."
                : $"{verb} {added} item(s) on {coordinator.RoomName}.");
        }
        catch (SonosException ex) when (ex.ErrorCode == 800)
        {
            _setStatus("The player refused that Spotify item. Check Spotify is linked in the " +
                       "Sonos app and that the account is Premium.");
            _log($"Sonos refused the Spotify URI (UPnP 800): {ex.Message}");
        }
        catch (Exception ex)
        {
            _setStatus($"Could not queue that: {ex.Message}");
            _log($"Spotify enqueue failed: {ex}");
        }
        finally { IsBusy = false; }
    }

    // ================================================================ Sonos playlists

    /// <summary>
    /// Saves whatever is in the queue as a playlist on the players. Because it is the player's
    /// own queue being saved, a playlist can mix Spotify tracks with files served from this PC —
    /// which no Spotify playlist could hold.
    /// </summary>
    [RelayCommand]
    private async Task SaveQueueAsPlaylistAsync()
    {
        var coordinator = _coordinator();
        if (coordinator is null) { _setStatus("Select a room first."); return; }

        var name = NewPlaylistName?.Trim() ?? "";
        if (name.Length == 0) { _setStatus("Give the playlist a name first."); return; }
        if (_queueLength() == 0) { _setStatus("The queue is empty — nothing to save."); return; }

        IsBusy = true;
        try
        {
            await coordinator.SaveQueueAsync(name);
            NewPlaylistName = "";
            _setStatus($"Saved the queue as “{name}”.");

            if (Source == MusicSource.SonosPlaylists) Show(await LoadSonosPlaylistsAsync());
        }
        catch (Exception ex)
        {
            _setStatus($"Could not save the playlist: {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync()
    {
        var coordinator = _coordinator();
        if (coordinator is null) { _setStatus("Select a room first."); return; }

        var target = EffectiveSelection.FirstOrDefault(i => i.Kind == MusicItemKind.SonosPlaylist);
        if (target is null) { _setStatus("Pick a Sonos playlist to delete."); return; }

        IsBusy = true;
        try
        {
            await coordinator.DestroyObjectAsync(target.Id);
            _setStatus($"Deleted the playlist “{target.Title}”.");
            Show(await LoadSonosPlaylistsAsync());
        }
        catch (Exception ex)
        {
            _setStatus($"Could not delete the playlist: {ex.Message}");
        }
        finally { IsBusy = false; }
    }
}
