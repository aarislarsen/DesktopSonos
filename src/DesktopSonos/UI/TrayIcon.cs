using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DesktopSonos.UI;

/// <summary>
/// The notification-area icon.
///
/// WPF has no tray icon, and the usual answer — WinForms' NotifyIcon — means switching
/// UseWindowsForms on, which drops System.Windows.Forms and System.Drawing into the project's
/// global usings and makes TextBox, KeyEventArgs, Brush and friends ambiguous throughout the WPF
/// code. Calling Shell_NotifyIcon directly avoids all of that, and the context menu can then be a
/// normal WPF ContextMenu that picks up the app's dark theme.
///
/// The window stays alive while hidden, which is the point: playback, eventing and the media
/// server keep running with no window on screen.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;

    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;
    private const int NifInfo = 0x00000010;

    private const int NiifInfo = 0x00000001;

    private readonly HwndSource? _source;
    private readonly ContextMenu? _menu;
    private readonly string _tooltip;

    private IntPtr _iconHandle;
    private bool _ownsIcon;
    private bool _added;
    private bool _hasShownHint;
    private bool _disposed;

    public TrayIcon(string tooltip)
    {
        _tooltip = tooltip;

        try
        {
            // A plain window with no style bits: never shown, never in the taskbar, but it has a
            // handle and a message loop, which is all Shell_NotifyIcon needs to call back.
            _source = new HwndSource(0, 0, 0, 0, 0, "DesktopSonosTrayWindow", IntPtr.Zero);
            _source.AddHook(WndProc);

            (_iconHandle, _ownsIcon) = LoadIconHandle();
            _menu = BuildMenu();
        }
        catch
        {
            // Without a working icon the window must never be hidden — see IsAvailable.
            _source = null;
        }
    }

    /// <summary>False when the icon could not be created, so callers keep the window visible.</summary>
    public bool IsAvailable => _source != null;

    public event Action? RestoreRequested;
    public event Action? ExitRequested;

    public bool Visible
    {
        get => _added;
        set
        {
            if (value) Add();
            else Remove();
        }
    }

    /// <summary>Says where the window went, once per run. Repeating it would be noise.</summary>
    public void ShowHintOnce(string title, string text)
    {
        if (!_added || _hasShownHint) return;
        _hasShownHint = true;

        var data = NewData();
        data.uFlags = NifInfo;
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(text, 255);
        data.dwInfoFlags = NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private void Add()
    {
        if (_added || _source is null) return;

        var data = NewData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _iconHandle;
        data.szTip = Trim(_tooltip, 127);

        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private void Remove()
    {
        if (!_added || _source is null) return;

        var data = NewData();
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
    }

    private NotifyIconData NewData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _source!.Handle,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != CallbackMessage) return IntPtr.Zero;

        switch ((int)lParam & 0xFFFF)
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                handled = true;
                RestoreRequested?.Invoke();
                break;

            case WmRButtonUp:
            case WmContextMenu:
                handled = true;
                ShowMenu();
                break;
        }

        return IntPtr.Zero;
    }

    private ContextMenu BuildMenu()
    {
        var show = new MenuItem { Header = "Show DesktopSonos" };
        show.Click += (_, _) => RestoreRequested?.Invoke();

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(show);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private void ShowMenu()
    {
        if (_menu is null || _source is null) return;

        // Without this the menu will not close when the user clicks elsewhere — the documented
        // quirk of showing a menu for a tray icon.
        SetForegroundWindow(_source.Handle);
        _menu.IsOpen = true;
    }

    /// <summary>
    /// Loads Assets\logo.ico. LR_DEFAULTSIZE picks the frame Windows wants for the tray, which is
    /// why the .ico carries small sizes rather than only the 1024 px artwork. The flag says
    /// whether the handle is ours to destroy — shared system icons are not.
    /// </summary>
    private static (IntPtr Handle, bool Owned) LoadIconHandle()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
            if (File.Exists(path))
            {
                const int imageIcon = 1;
                const int loadFromFile = 0x00000010;
                const int defaultSize = 0x00000040;

                var handle = LoadImage(IntPtr.Zero, path, imageIcon, 0, 0, loadFromFile | defaultSize);
                if (handle != IntPtr.Zero) return (handle, true);
            }
        }
        catch
        {
            // Fall through to a stock icon rather than showing nothing.
        }

        try
        {
            return (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false);   // IDI_APPLICATION
        }
        catch
        {
            return (IntPtr.Zero, false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Skipping this leaves a dead icon in the tray until the user hovers over it.
        Remove();

        if (_iconHandle != IntPtr.Zero && _ownsIcon)
        {
            try { DestroyIcon(_iconHandle); } catch { }
        }

        _iconHandle = IntPtr.Zero;

        try
        {
            _source?.RemoveHook(WndProc);
            _source?.Dispose();
        }
        catch { }
    }

    // ---------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public int uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, int type,
        int width, int height, int load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}
