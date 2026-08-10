Two files here are used by the app:

    logo.png    shown in the square tile at the top-left of the window
    logo.ico    the window, taskbar, Alt-Tab and notification-area icon, and
                the icon compiled into DesktopSonos.exe (ApplicationIcon)

logo.png is drawn with padding and Stretch="Uniform", so any aspect ratio
works; use a transparent background, since the tile behind it is #1D2127. If it
is missing or unreadable the app falls back to the drawn placeholder mark.

logo.ico was generated from logo.png and carries 16, 20, 24, 32, 40, 48, 64,
128 and 256 px frames so Windows can pick a sharp one for each place it appears
rather than downscaling the 1024 px bitmap. After replacing logo.png, rebuild
the .ico from it — any icon converter will do, as long as it keeps the small
sizes. If logo.ico is missing the app falls back to the icon compiled into the
executable.

Both are copied next to the executable at build time. Nothing else in this
folder is used; everything here is copied to the output directory as-is.
