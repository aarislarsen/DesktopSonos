using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopSonos.Library;
using DesktopSonos.UI;
using DesktopSonos.ViewModels;

namespace DesktopSonos;

public partial class MainWindow : Window
{
    private const double CollapsedWidth = 46;

    private readonly MainViewModel _viewModel = new();
    private double _expandedRoomsWidth = 268;
    private double _expandedLibraryWidth = 404;
    private TrayIcon? _tray;

    /// <summary>The full window's size, kept while the compact strip is on screen.</summary>
    private double _fullWidth = 1480;
    private double _fullHeight = 900;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.PromptForPassword = (share, user) => PasswordWindow.Prompt(this, share, user);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;

        // Logging off or shutting Windows down has to run the same cleanup as clicking close,
        // otherwise an application would be left routed to an output the user cannot hear.
        if (Application.Current != null)
            Application.Current.SessionEnding += (_, _) => Close();
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
            _tray?.Dispose();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var (width, height) = _viewModel.RestoreWindowSize();
        if (width > 400 && height > 300)
        {
            Width = width;
            Height = height;
        }

        // Remembered before StartServices, which may switch straight to the compact strip.
        _fullWidth = Width;
        _fullHeight = Height;

        LoadLogo();
        SetUpTray();
        _viewModel.StartServices();

        // StartServices restores the expanded/collapsed flags, so widths are applied after it.
        var (rooms, library) = _viewModel.RestoreColumnWidths();
        if (rooms > CollapsedWidth) _expandedRoomsWidth = rooms;
        if (library > CollapsedWidth) _expandedLibraryWidth = library;
        ApplyRoomsWidth();
        ApplyLibraryWidth();

        // Search is the most common next action, so it starts focused — but only when it is on
        // screen: focusing a control inside a collapsed panel silently does nothing.
        if (_viewModel.IsLibraryExpanded && !_viewModel.IsCompactView) SearchBox.Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel.IsCompactView)
        {
            // The strip's own width is what changed; the full window keeps the size it had.
            _viewModel.CompactWidth = Width;
            _viewModel.PersistWindowSize(_fullWidth, _fullHeight);
        }
        else if (WindowState == WindowState.Normal)
        {
            _viewModel.PersistWindowSize(Width, Height);
        }

        var rooms = _viewModel.IsRoomsExpanded ? RoomsColumn.ActualWidth : _expandedRoomsWidth;
        var library = _viewModel.IsLibraryExpanded ? LibraryColumn.ActualWidth : _expandedLibraryWidth;
        _viewModel.PersistLayout(rooms, library);
    }

    // ---------------------------------------------------------------- notification area

    private void SetUpTray()
    {
        _tray = new TrayIcon("DesktopSonos");
        _tray.RestoreRequested += () => Dispatcher.Invoke(RestoreFromTray);
        _tray.ExitRequested += () => Dispatcher.Invoke(() =>
        {
            // Shown again first so Closing sees real dimensions to remember.
            RestoreFromTray();
            Close();
        });
    }

    /// <summary>
    /// Minimising puts the app in the notification area instead of the taskbar. Nothing stops:
    /// the queue, eventing and the media server all keep running with no window on screen.
    /// </summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        if (!_viewModel.MinimizeToTray) return;
        if (_tray is null || !_tray.IsAvailable) return;

        _tray.Visible = true;
        ShowInTaskbar = false;
        Hide();
        _tray.ShowHintOnce("DesktopSonos is still running",
            "Playback carries on. Click this icon to bring the window back.");
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();

        // Restoring from a hidden window does not always come to the front on its own.
        Topmost = true;
        Topmost = false;

        if (_tray != null) _tray.Visible = false;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsRoomsExpanded):
                ApplyRoomsWidth();
                break;
            case nameof(MainViewModel.IsLibraryExpanded):
                ApplyLibraryWidth();
                break;
            case nameof(MainViewModel.IsCompactView):
                ApplyViewMode();
                break;
        }
    }

    /// <summary>
    /// Resizes the window around whichever layout is showing. The compact strip is fixed in
    /// height, stays on top and can be dragged anywhere — the point is to leave it on screen.
    /// </summary>
    private void ApplyViewMode()
    {
        // Measured from the strip's own contents: 15 px of margins, a 30 px button row, a 30 px
        // transport row, a ~26 px seek row and five 19 px queue rows under a 6 px gap, with a
        // little slack for display scaling.
        const double compactHeight = 222;

        if (_viewModel.IsCompactView)
        {
            if (WindowState == WindowState.Normal && Height > compactHeight * 1.5)
            {
                _fullWidth = Width;
                _fullHeight = Height;
            }

            WindowState = WindowState.Normal;

            // Minimums come down before the size does, or WPF simply refuses to shrink.
            MinWidth = 340;
            MinHeight = compactHeight;
            MaxHeight = compactHeight;

            Width = Math.Max(340, _viewModel.CompactWidth);
            Height = compactHeight;
            Topmost = true;
        }
        else
        {
            _viewModel.CompactWidth = Width;

            MaxHeight = double.PositiveInfinity;
            MinWidth = 1080;
            MinHeight = 640;

            Width = _fullWidth;
            Height = _fullHeight;
            Topmost = false;
        }
    }

    /// <summary>
    /// The rails collapse sideways rather than upwards, so remember how wide the user had them.
    /// </summary>
    private void ApplyRoomsWidth()
    {
        if (_viewModel.IsRoomsExpanded)
        {
            RoomsColumn.Width = new GridLength(_expandedRoomsWidth);
            return;
        }

        if (RoomsColumn.ActualWidth > CollapsedWidth + 10)
            _expandedRoomsWidth = RoomsColumn.ActualWidth;

        RoomsColumn.Width = new GridLength(CollapsedWidth);
    }

    private void ApplyLibraryWidth()
    {
        if (_viewModel.IsLibraryExpanded)
        {
            LibraryColumn.Width = new GridLength(_expandedLibraryWidth);
            return;
        }

        if (LibraryColumn.ActualWidth > CollapsedWidth + 10)
            _expandedLibraryWidth = LibraryColumn.ActualWidth;

        LibraryColumn.Width = new GridLength(CollapsedWidth);
    }

    /// <summary>
    /// Uses Assets\logo.png next to the executable for the panel, and Assets\logo.ico for the
    /// window itself — the taskbar button and Alt-Tab read that one, and an .ico carries small
    /// sizes drawn for the job rather than a downscaled 1024 px bitmap.
    /// </summary>
    private void LoadLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(path))
            {
                LogoImage.Source = LoadFrozen(path);
                LogoImage.Visibility = Visibility.Visible;
                LogoFallback.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // Keep the drawn placeholder if the file is unreadable.
        }

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
            if (File.Exists(iconPath)) Icon = LoadFrozen(iconPath);
        }
        catch
        {
            // The compiled-in application icon still applies.
        }
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // so the file is not kept locked
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ---------------------------------------------------------------- window chrome

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- lists

    /// <summary>ListView.SelectedItems is not bindable, so push the multi-selection across.</summary>
    private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SetSelectedTracks(TrackList.SelectedItems.Cast<TrackInfo>());

    private async void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is null) return;
        await _viewModel.PlayNextCommand.ExecuteAsync(null);
    }

    private async void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is not QueueItemViewModel item) return;
        await _viewModel.PlayQueueItemAsync(item);
    }

    private async void CompactQueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CompactQueueList.SelectedItem is not QueueItemViewModel item) return;
        await _viewModel.PlayQueueItemAsync(item);
    }

    private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _viewModel.BeginSeek();

    private async void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        await _viewModel.CommitSeekAsync(PositionSlider.Value);

    private async void CompactPositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        await _viewModel.CommitSeekAsync(CompactPositionSlider.Value);

    // ---------------------------------------------------------------- keyboard

    private static bool IsTypingTarget(IInputElement? focused) => focused is TextBox or PasswordBox;

    /// <summary>
    /// Searching must work even when the library is collapsed, so this opens it first and waits
    /// for layout before moving focus — a control inside a collapsed panel cannot take focus.
    /// </summary>
    private void FocusSearch(string? append)
    {
        // There is no library on screen in the compact strip, so typing must not try to reach it.
        if (_viewModel.IsCompactView) return;

        if (!_viewModel.IsLibraryExpanded)
        {
            _viewModel.IsLibraryExpanded = true;
            Dispatcher.BeginInvoke(new Action(() => ApplySearchFocus(append)), DispatcherPriority.Input);
            return;
        }

        ApplySearchFocus(append);
    }

    private void ApplySearchFocus(string? append)
    {
        SearchBox.Focus();

        if (append is null)
        {
            SearchBox.SelectAll();
            return;
        }

        _viewModel.FilterText += append;
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    protected override async void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        var typing = IsTypingTarget(Keyboard.FocusedElement);
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (ctrl && e.Key == Key.Z)
        {
            e.Handled = true;
            await _viewModel.UndoCommand.ExecuteAsync(null);
            return;
        }

        if (ctrl && e.Key == Key.M)
        {
            e.Handled = true;
            _viewModel.ToggleCompactViewCommand.Execute(null);
            return;
        }

        if (ctrl && e.Key == Key.F)
        {
            e.Handled = true;
            FocusSearch(null);
            return;
        }

        // The activity log is the first thing to reach for when something misbehaves, so it
        // gets a shortcut rather than living only behind a button in a collapsible panel.
        if (ctrl && e.Key == Key.L)
        {
            e.Handled = true;
            _viewModel.ToggleSettingsCommand.Execute(null);
            return;
        }

        if (ctrl && (e.Key == Key.Up || e.Key == Key.Down))
        {
            e.Handled = true;
            await _viewModel.NudgeVolumeAsync(e.Key == Key.Up ? 4 : -4);
            return;
        }

        // Numpad + / - work without a modifier, since the numeric keypad is never used for typing
        // in this window — but only when the caret is not in a text box, where they are characters.
        if (!typing && (e.Key == Key.Add || e.Key == Key.Subtract))
        {
            e.Handled = true;
            await _viewModel.NudgeVolumeAsync(e.Key == Key.Add ? 4 : -4);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape when typing:
                e.Handled = true;
                _viewModel.FilterText = "";
                Keyboard.ClearFocus();
                QueueList.Focus();
                return;

            case Key.Escape when _viewModel.IsSettingsOpen:
                e.Handled = true;
                _viewModel.ToggleSettingsCommand.Execute(null);
                return;

            case Key.Space when !typing:
                e.Handled = true;
                await _viewModel.PlayPauseCommand.ExecuteAsync(null);
                return;

            case Key.Enter when TrackList.IsKeyboardFocusWithin:
                e.Handled = true;
                await _viewModel.PlayNextCommand.ExecuteAsync(null);
                return;

            case Key.Delete when QueueList.IsKeyboardFocusWithin:
                e.Handled = true;
                await _viewModel.RemoveFromQueueCommand.ExecuteAsync(null);
                return;
        }
    }

    /// <summary>
    /// Raskin's incremental search: typing anywhere goes to the search field instead of being
    /// swallowed, so there is no "click here first" step.
    /// </summary>
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (e.Handled || _viewModel.IsSettingsOpen || _viewModel.IsCompactView) return;
        if (IsTypingTarget(Keyboard.FocusedElement)) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;

        FocusSearch(e.Text);
        e.Handled = true;
    }
}
