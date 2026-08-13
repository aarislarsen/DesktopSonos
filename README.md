# DesktopSonos
<img width="1480" height="700" alt="image" src="https://github.com/user-attachments/assets/944f77d4-21e1-4730-8241-08cabf9cba58" />

A Windows desktop app (C# / WPF / .NET 8) that finds the Sonos players on your LAN and plays
audio to them from four sources:

1. **Local files** — MP3, FLAC, M4A, WAV, WMA, OGG, AIFF from any folder on the PC.
2. **NAS / network shares** — any UNC path (`\\nas\music`), with optional credentials.
3. **Live audio from this PC** — either everything the Windows mixer plays, or a single
   application (pick its window), which can be moved to another output so it plays only on Sonos.
4. **Streaming services** — Spotify, YouTube Music, and anything else linked in the Sonos app:
   your favourites, saved playlists, pasted Spotify links, and (optionally) Spotify search. The
   players stream it themselves from the account already linked to the household, so it goes in
   the same queue as everything else.

---

## What this version adds

A **MUSIC** tab beside **LIBRARY** in the right-hand panel, which puts streaming content into the
same queue, with the same five verbs, as the files on your disk. The players do the streaming
themselves from the account already linked in the Sonos app, so nothing is captured, transcoded or
routed through this PC — and Sonos' own queue, grouping, transport and eventing all keep working
exactly as before.

| | Works with nothing set up | Needs the optional Spotify sign-in |
|---|---|---|
| **Sonos favourites** — albums, playlists, tracks, stations, from every service you have linked | ✅ | |
| **Sonos playlists** — list them, and open one to queue its tracks individually | ✅ | |
| **Save the queue as a Sonos playlist** — can mix Spotify, YouTube Music and your NAS files in one list | ✅ | |
| **Paste a Spotify link or URI** to queue an album, playlist or track | ✅ | |
| **Service column and filter**, named by the players themselves | ✅ | |
| **Free-text Spotify search** over tracks, albums and playlists | | ✅ |
| **Your Spotify playlists, saved albums and liked songs** | | ✅ |

The sign-in is optional, read-only, and explained in full under
[The Spotify client id](#the-spotify-client-id-and-the-optional-sign-in). Skip it and everything
in the left column still works.

**One prerequisite, and it is not optional:** the service has to be linked in the Sonos app, on
whatever subscription it needs. This app tells the players what to play; it never holds the music.

**Only Spotify gets search.** Its Sonos URI carries a public id, so any track can be named and
then built. YouTube Music's is an opaque token issued against your household — see
[Why Spotify gets search and YouTube Music does not](#why-spotify-gets-search-and-youtube-music-does-not).
For YouTube Music, favourite things in the Sonos or YouTube Music app and they appear here, or
open a saved queue and take tracks out of it.

---

## Quick start

```powershell
git clone https://github.com/<owner>/DesktopSonos.git
cd DesktopSonos
dotnet publish src\DesktopSonos\DesktopSonos.csproj -c Release -r win-x64 --self-contained true -o publish
Copy-Item publish "$env:LOCALAPPDATA\Programs\DesktopSonos" -Recurse
& "$env:LOCALAPPDATA\Programs\DesktopSonos\DesktopSonos.exe"
```

Allow it through the firewall on **Private** networks when Windows asks. The sections below explain
each step, and what to do when one of them does not go to plan.

---

## Requirements

| | |
|---|---|
| OS | Windows 10 1803 or newer. Per-application capture needs build 20348+ (Windows 11); everything else works on 10. |
| SDK | .NET 8 SDK — `winget install Microsoft.DotNet.SDK.8` |
| Editor | VS Code with the **C# Dev Kit** extension, or Visual Studio 2022 17.8+ |
| Network | The PC and the players on the same subnet. No Sonos account, no API key, no internet — except for Spotify, which needs the players online and Spotify linked in the Sonos app. |

The project targets `net8.0-windows` with `PlatformTarget=x64`. The x64 pin is not cosmetic: the
LAME encoder in `NAudio.Lame` ships architecture-specific native DLLs, and AnyCPU picks the wrong
one. NuGet packages: `NAudio`, `NAudio.Lame`, `CommunityToolkit.Mvvm`, `TagLibSharp` and
`System.Security.Cryptography.ProtectedData` — all restored automatically.

---

## Get the source

Install [Git for Windows](https://git-scm.com/download/win) if you have not already, then clone the
repository and step into it:

```powershell
git clone https://github.com/<owner>/DesktopSonos.git
cd DesktopSonos
```

Replace `<owner>` with wherever the repository actually lives; if you were given an SSH URL, use
`git clone git@github.com:<owner>/DesktopSonos.git` instead. No Git at all? Download the ZIP from
the repository's **Code** button, extract it, and carry on from the extracted folder — the build
does not need Git for anything.

You should now be sitting on:

```
DesktopSonos\
├── DesktopSonos.sln
├── README.md
└── src\DesktopSonos\
    ├── DesktopSonos.csproj
    └── Assets\            logo.png, logo.ico
```

To pick up later changes: `git pull`, then build again. Nothing in `%APPDATA%\DesktopSonos` is
touched by a rebuild, so your speakers, library and settings survive it.

---

## Build

Check the SDK is there and is version 8 or newer — `dotnet --version`. Then, from the repo root:

```powershell
dotnet restore                 # pulls NAudio, NAudio.Lame, CommunityToolkit.Mvvm, TagLibSharp
dotnet build -c Release        # compiles; warnings are fine, errors are not
dotnet run --project src\DesktopSonos\DesktopSonos.csproj    # run it straight from source
```

The first `restore` needs internet access; after that you can build offline.

In VS Code: open the repo folder, let the **C# Dev Kit** extension finish loading `DesktopSonos.sln`
(watch the status bar), then press **F5**. In Visual Studio 2022: open the `.sln`, set
`DesktopSonos` as the startup project, **F5**.

Two build failures worth naming:

- *`Assets\logo.ico` not found* — the icon is missing from `src/DesktopSonos/Assets/`. Restore it,
  or delete the `<ApplicationIcon>` line from `DesktopSonos.csproj`. See
  `src/DesktopSonos/Assets/README.txt`.
- *`NETSDK1045` or a missing `net8.0-windows` target* — the installed SDK is older than 8. Install
  the .NET 8 SDK and reopen the terminal so the new `dotnet` is on `PATH`.

---

## Install

There is no installer, no elevation and no registry use. You publish the app to a folder, copy that
folder wherever you want it, and run the `.exe` inside.

### 1. Publish

Pick one of the two forms. Run it from the repo root:

```powershell
# framework-dependent — small output (~5 MB), needs the .NET 8 *Desktop* runtime on the machine
dotnet publish src\DesktopSonos\DesktopSonos.csproj -c Release -r win-x64 --self-contained false -o publish

# self-contained — carries its own runtime (~150 MB), runs on a machine with no .NET at all
dotnet publish src\DesktopSonos\DesktopSonos.csproj -c Release -r win-x64 --self-contained true -o publish
```

Use the self-contained form if you are copying to another PC and do not want to install anything on
it. For the framework-dependent form, the target machine needs the **.NET Desktop Runtime 8**
(`winget install Microsoft.DotNet.DesktopRuntime.8`) — the plain ASP.NET or console runtime is not
enough, WPF needs the desktop one.

### 2. Copy it across

Copy the **whole `publish` folder**, not just the `.exe`. What has to travel together:

| Item | Why |
|---|---|
| `DesktopSonos.exe` | the app |
| `*.dll` beside it | NAudio, LAME, TagLib, the MVVM toolkit, and (self-contained) the runtime |
| `libmp3lame.*.dll`, and any `runtimes\` folder | the native LAME encoder — streaming this PC fails without it. A RID-specific publish usually flattens it into the root; copy whatever is there. |
| `DesktopSonos.runtimeconfig.json`, `.deps.json` | tell the runtime what to load |
| `Assets\logo.png`, `Assets\logo.ico` | window icon, notification-area icon and the panel logo, all read at runtime |

Drop the folder somewhere that needs no elevation — `%LOCALAPPDATA%\Programs\DesktopSonos` is the
sensible default. Avoid `C:\Program Files`: writing there needs admin rights, and there is nothing
to gain from it.

If you moved the folder as a ZIP, or copied it over the network, Windows may mark the files as
downloaded and SmartScreen will interrupt the first launch. Either click **More info → Run anyway**,
or clear the mark first:

```powershell
Get-ChildItem -Recurse "$env:LOCALAPPDATA\Programs\DesktopSonos" | Unblock-File
```

### 3. First run

1. Run `DesktopSonos.exe`. Right-click its taskbar button → **Pin to taskbar**, or make a Start
   menu shortcut, if you want it to hand.
2. Windows Firewall prompts the first time — allow it on **Private** networks. The app has to
   accept connections from the players, not just make them. See [Firewall](#firewall) if you
   dismissed the prompt.
3. Point it at your music: **Add folder…** in the library panel, or a UNC path under **⚙ → NAS
   path** for a NAS. For streaming, switch to the **MUSIC** tab — if Spotify or YouTube
   Music is linked in the Sonos app, your favourites are already there and nothing else needs
   setting up.
4. **To start with Windows**: press `Win+R`, run `shell:startup`, and drop a shortcut to
   `DesktopSonos.exe` in the folder that opens. It comes back in whichever shape it was closed in,
   compact strip included.

To update later: `git pull`, publish again, and copy the new `publish` folder over the old one.
Settings are kept elsewhere and are not disturbed.

Everything the app remembers lives in **`%APPDATA%\DesktopSonos`**:

| File | What it holds |
|---|---|
| `settings.json` | speakers, library folders, last room, panel widths and collapse state, window size, compact-view state, media-server port, stream bitrate and gain, output-routing choice, Spotify client id and the household's Spotify service numbers |
| `library.json` | the scanned library, so tracks are on screen before any folder is re-read |
| `spotify.json` | the optional Spotify sign-in. The refresh token in it is encrypted with DPAPI under your Windows account, so the file is useless on another machine or to another user |
| `stream-debug.mp3` | a capped copy of the last encoded stream, for checking what was actually sent |

To uninstall: delete the program folder and that settings folder. Nothing is written to the
registry and no services are installed.

---

## Interface

Laid out along the lines of Raskin's *The Humane Interface*:

- **Nothing is remembered by you, everything by the app.** Speakers, library folders and the
  scanned tracks themselves persist in `%APPDATA%\DesktopSonos\` (`settings.json` and
  `library.json`). On launch the rooms *and the whole track list* appear instantly from disk with
  no network traffic and no scanning; both are then verified in the background and swapped in one
  go if anything changed. You can play immediately — the refresh never blocks playback.
  Discovery and folder-picking are exceptions now, not the routine path.
- **Layout follows attention.** The queue — what is actually going to happen — sits in the
  centre; the library is a source you dip into, so it lives off to the side.
- **Double-click means "play this next"**, inserting after the current track rather than wiping
  the queue. Destroying a queue you have built is not a reasonable default for a double-click;
  "Play now" is still there as an explicit, named button.
- **No confirmation dialogs.** Destructive queue actions (shuffle, clear, remove) push an undo
  step instead. **Ctrl+Z** restores the previous queue exactly, and the status bar always names
  what would be undone.
- **Type anywhere to search.** Keystrokes that land nowhere are routed into the search field, so
  there is no "click the box first" step. **Esc** clears it.
- **Fixed positions.** Panels never reflow: rooms left, queue centre, library right, transport
  along the bottom. Collapsing a side panel moves it to the edge as a labelled strip rather than
  rearranging what is left. Habituation depends on controls not moving.
- **The same verbs mean the same thing everywhere.** Streaming content lives in a *tab* over the
  library column rather than in a fourth panel, precisely so nothing moves when you switch to it:
  the search box, the five verbs and the list are in the same places, and double-click still means
  "play this next". Where a source is genuinely different — a container you can open, a station
  that cannot be queued — the difference is stated rather than hidden.
- **One control, one meaning.** "Play next / Add to queue / Play now / Queue all / Shuffle in" are
  named verbs rather than one button whose behaviour depends on hidden state.

Every shortcut is listed under [Using it](#using-it).

The window uses `WindowChrome` for a thin custom frame with its own minimise / maximise / close
buttons. Scrollbars, sliders, dropdowns and list headers are all templated — none of the stock
Windows chrome survives.

Whatever is on screen at exit comes back at the next start: window size, both panel widths, and
whether the **Rooms** and **Library** panels were collapsed.

### Compact player
**Ctrl+M**, or the ▭ button in the title bar, swaps the window for a strip about 470 × 222: what
is playing, play/previous/next, the seek bar, the room and its volume, and five rows of the queue
(scroll for the rest; double-click an entry to jump to it). It stays on top and can sit along an
edge of the screen all day. ⤢ goes back.

It is the same window with a second layout, not a second window, so playback, eventing and any
running stream carry straight across. The full window's size is kept while the strip is up and
restored on the way back, the strip's own width is remembered separately, and which of the two was
on screen at exit is what opens next time.

### Minimising
Minimising hides the window and leaves a **notification-area icon**. Nothing stops while it is
hidden: playback, GENA eventing, desktop streaming and the media server all keep running. Click
the icon to bring the window back; right-click gives *Show* and *Exit*.

`UI/TrayIcon.cs` calls `Shell_NotifyIcon` directly against a styleless `HwndSource`. The usual
answer is WinForms' `NotifyIcon`, but that means `UseWindowsForms`, which adds
`System.Windows.Forms` and `System.Drawing` to the project's global usings and makes `TextBox`,
`KeyEventArgs`, `Brush` and others ambiguous across the WPF code. The interop route avoids that,
and the menu is then a normal WPF `ContextMenu` that inherits the dark theme.

Set `"MinimizeToTray": false` in `settings.json` for an ordinary taskbar minimise instead. If the
icon cannot be created for any reason the window is never hidden, so it cannot go missing.

### Your logo
Drop a transparent PNG at `src/DesktopSonos/Assets/logo.png` and rebuild. It fills the square tile
at the top-left of the rail (`Stretch="Uniform"`, so any aspect ratio works). Without it the app
draws a placeholder mark.

`Assets/logo.ico` is the same artwork as a multi-size icon (16–256 px). It is compiled into the
executable via `ApplicationIcon` and loaded at runtime for the window, so Explorer, the taskbar,
Alt-Tab and the tray all show it. Regenerate it whenever the PNG changes — see
`Assets/README.txt`.

## Using it

**Rooms** are on the left, the **queue** is in the middle, and the right-hand panel carries two
tabs — **LIBRARY** for music on this PC and the network, **MUSIC** for Spotify, YouTube Music and
anything else linked in the Sonos app. The transport bar runs along the bottom. Both side panels
collapse sideways with the ▾ / ▸ button in their headers, which is also how you stop a stray click
from changing which room is playing. Whichever tab was up at exit is the one that opens next time.

### Rooms
Speakers are found automatically on the first run and remembered afterwards, so the panel is
usually already populated. **find** re-scans if something new appears. Click a room to make it the
target — commands are sent to the room's *group coordinator* automatically, because Sonos rejects
transport commands aimed at a grouped follower. Tick several rooms and use **Group** / **Ungroup**
to join or split them.

Nothing found? Open **⚙**, type a player's IP (Sonos app → Settings → System → About) and click
**Add by IP**. One reachable player is enough — the whole household is read from it.

### Library
**Add folder…** for local folders. For a NAS, open **⚙**, type the UNC path (`\\nas\music`), add a
user name if the share needs one, and click **Add share**. Folders are remembered and re-scanned in
the background at every start; **Rescan** forces it.

Type anywhere to search — no need to click the box first. Ctrl/Shift-click selects several tracks.
Then one of five verbs, in order of how often you will want them:

| Button | What it does |
|---|---|
| **Play next** | Inserts after the current track. Also what double-click and **Enter** do. |
| **Add to queue** | Appends to the end, without interrupting anything. |
| **Play now** | Replaces the whole queue with the selection and plays it. |
| **Queue all** | Replaces the queue with every track currently listed (i.e. matching the search). |
| **Shuffle in** | Appends tracks drawn at random from the library until the queue reaches the 500-entry Sonos limit. |

**Shuffle in** exists because a Sonos queue holds at most 500 entries, so a library larger than
that cannot be queued whole and shuffled on the speaker. Drawing a fresh random selection is the
practical equivalent — no track is drawn twice within one draw, and a search filter, if one is
active, narrows what gets drawn from. Press it again after **Clear** for a different 500.

### Music (Spotify, YouTube Music, and whatever else you have linked)
**MUSIC**, the tab beside **LIBRARY**, lists streaming content and queues it on the players with
the same five verbs the library uses. What plays is the real thing at full quality: the players
fetch it from the service themselves, exactly as the Sonos app makes them. Nothing is captured,
re-encoded or downloaded, and this app is not in the audio path at all.

That also means **the service has to be linked in the Sonos app**, on whatever subscription it
needs — Spotify Premium, YouTube Music via a YouTube Premium subscription, and so on. That is the
one prerequisite, and it is not something this app can do for you.

Where the list spans more than one service, a **SERVICE** dropdown appears and a **SERVICE**
column names each row. Both are built from what is actually in the household — service names come
from the players, so a service this app has never heard of is still named correctly.

| Button | What it lists | Services | Needs a sign-in? |
|---|---|---|---|
| **Favourites** | everything favourited in the Sonos app — albums, playlists, tracks, stations | all of them | no |
| **Sonos** | playlists saved on the players; open one to see and queue single tracks | all of them | no |
| **Search** | free-text search over tracks, albums and playlists | Spotify only | yes — [see below](#the-spotify-client-id-and-the-optional-sign-in) |
| **Playlists** / **Albums** / **Liked** | your own playlists, saved albums and liked songs | Spotify only | yes — [see below](#the-spotify-client-id-and-the-optional-sign-in) |

Plus the **paste box**: drop in any `https://open.spotify.com/album/…` link or `spotify:album:…`
URI, press **Add**, and it appears in the list ready to queue. Nothing is signed in for that
either — the id is in the link and the players resolve it against their own Spotify account.

Double-click an album, a playlist or a saved queue to see what is inside it; **◂** goes back.
Double-click a track to play it next. A station can only be played on its own with **Play now** —
Sonos will not put a live stream in a queue.

Opening a **saved queue** is the useful one for services that cannot be searched. A saved queue on
a real household came back as 100 individually queueable YouTube Music tracks, each with its
title, artist and running time — because the entries are handed back to the player exactly as it
gave them out, service token and all, rather than being rebuilt from anything.

The five verbs work as they do in the library, with two differences worth knowing:

- **An album or playlist is enqueued as one container**, and the player expands it into entries
  itself. A 60-track playlist is one SOAP call, not sixty, so it lands more or less instantly.
- **Shuffle in** draws only from single playable rows — tracks and favourites, not containers and
  not stations. A random album would arrive as a block, which is not what shuffling means.

**YouTube Music, and any service that is not Spotify, works through Favourites and Sonos
playlists only.** There is no search and no paste box for them, and the reason is not laziness —
see [Streaming without an account of our own](#streaming-without-an-account-of-our-own). The
practical routine is to favourite things in the Sonos or YouTube Music app once; they show up
here immediately and queue like anything else.

#### The Spotify client id, and the optional sign-in

**Skip this entirely unless you want free-text Spotify search.** Favourites, Sonos playlists and
pasted Spotify links all play without it, because the players use the Spotify account already
linked in the Sonos app. Nothing below affects what the speakers can play — only whether this app
can look things up.

**What a client id is.** It identifies *the application* to Spotify, not you. Spotify's Web API
refuses anonymous requests, and it has no shared id that third-party apps may use — every app
that talks to it registers its own and quotes it on each call. So there is no id to ship inside
DesktopSonos: yours has to be yours. It takes about two minutes, once, and costs nothing.

**What it is not.** It is not a password, not a subscription, and not a secret. A client id is a
public identifier and is fine sitting in `settings.json` in plain text — Spotify's own docs treat
it as public. It grants nothing on its own: your account is only reached after *you* approve the
sign-in in the browser, and even then only for reading (below).

**Getting one:**

1. Go to [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) and log in
   with your ordinary Spotify account. A free account works; a developer subscription is not a
   thing and nothing is charged.
2. **Create app**. *App name* and *App description* can be anything — "DesktopSonos" is fine.
   Nobody but you sees them, and the app stays in development mode, which is all this needs.
3. In **Redirect URIs**, add exactly what the settings drawer shows:

   ```
   http://127.0.0.1:8098/callback
   ```

   Then press **Add** beside the field — a URI typed but not added is not saved, which is the
   single most common thing to get wrong here. It must match character for character; `localhost`
   in place of `127.0.0.1` is rejected by Spotify.
4. Under **Which API/SDKs are you planning to use**, tick **Web API**. Leave the rest alone.
5. **Save**, then open the app's **Settings**. The **Client ID** is at the top — copy it. There is
   also a **Client secret** behind a "View client secret" link: **you do not need it**. This app
   uses the PKCE flow, which exists precisely because a desktop program cannot keep a secret, so
   there is nothing to paste and nothing to leak.

**Using it:** open the **MUSIC** tab → **⚙** → paste into **CLIENT ID** → **Connect Spotify**.
Your default browser opens Spotify's approval page; if you are already signed in there it is one
click. The app catches the redirect on `127.0.0.1:8098`, and the link is remembered from then on —
you are not asked again.

**What it can do once connected.** Read-only, and only these scopes: your profile, your saved
albums and liked songs, your playlists, and search. There is no write scope of any kind, so this
app cannot alter your Spotify library, follows or playlists even by accident. Playlists made here
are saved on the players instead (below). The refresh token is stored in
`%APPDATA%\DesktopSonos\spotify.json`, encrypted with DPAPI under your Windows account — copying
that file to another PC or another user gets nothing. **Disconnect** deletes it.

**If it goes wrong:**

| What you see | Cause |
|---|---|
| `INVALID_CLIENT: Invalid redirect URI` | The redirect URI on the dashboard does not match. Check for a trailing slash, `localhost` instead of `127.0.0.1`, or that you never pressed **Add**. |
| `INVALID_CLIENT: Invalid client` | The client id is wrong — a truncated paste, or the client *secret* pasted by mistake. |
| "Port 8098 is already in use" | Something else on the PC has the port. Change **REDIRECT PORT** in the drawer, then add the new URI it shows to the dashboard too — both sides have to agree. |
| Browser opens, nothing happens after approving | A firewall is blocking the loopback callback, or the browser is on a different machine than the app. |

#### Making playlists
**Sonos** → name it → **Save queue** writes whatever is in the queue now as a playlist on the
players. It is a *Sonos* playlist, not a Spotify one, and that is the point: it can hold Spotify
tracks and files served from this PC in the same list, which no Spotify playlist could. It shows
up in the Sonos app too. **Delete** removes the selected one.

### Queue
The centre panel is the player's *real* queue, read back from it — changes made in the Sonos app
show up here too. Double-click an entry to jump to it, reorder with ↑ / ↓, **Remove** and
**Clear** edit in place, **⤮ Shuffle** randomises the order. Every one of those is undoable with
**Ctrl+Z**.

**Shuffle** reorders the entries on the player itself, one move per entry, rather than emptying the
queue and filling it back up. It is slower on a long queue — a few hundred round trips — but the
player keeps its own metadata for each entry, nothing is lost if a single move is refused, and
playback carries on through it.

▶ / ❚❚ *resumes*; with nothing loaded it falls back to playing the current library selection
rather than failing.

### Streaming this PC
Under the queue. Pick a source — a whole output device, or a single application window (a ♪ marks
apps making sound right now) — and press **Start**. ↻ re-scans open windows. Changing the source
while streaming switches over on the spot, without dropping the connection to the speaker.

To hear an application on Sonos and **not** on the PC, set **Send app to** to an output nothing is
plugged into. The app is moved there while you stream and put back when you stop. Left on *Leave it
where it is*, the app plays in both places at once.

If the level readout looks low, the captured device's own Windows volume is the cause — loopback
taps the mix *after* it. Set that device to 100% and use the Sonos volume instead, or raise
**Gain**.

### Compact player and tray
**Ctrl+M** (or ▭) shrinks everything to a small always-on-top strip: what is playing, transport,
seek bar, volume and five scrollable rows of the queue. ⤢ brings the full window back. **—** hides the window to the notification
area, where the app keeps playing; click the icon to bring it back.

### Keyboard

| Key | |
|---|---|
| **Space** | play / pause |
| **Enter** | play the library selection next |
| **Delete** | remove the selected queue entry |
| **Ctrl+↑ / Ctrl+↓**, or numpad **+ / −** | room volume |
| **Ctrl+F**, or just start typing | search the library, or Spotify if the MUSIC tab is up |
| **Esc** | clear the search, or close the drawer |
| **Ctrl+Z** | undo the last queue change |
| **Ctrl+M** | compact player |
| **Ctrl+L** | activity log |

---

## How it works

### Discovery
SSDP `M-SEARCH` is multicast to `239.255.255.250:1900` with
`ST: urn:schemas-upnp-org:device:ZonePlayer:1`, sent from **every** LAN interface (multi-NIC,
Hyper-V and WSL machines otherwise probe the wrong adapter). Responders' `LOCATION` headers give
their IP; `http://<ip>:1400/xml/device_description.xml` gives the room name, model and
`RINCON_…` UUID. Then a single `ZoneGroupTopology#GetZoneGroupState` call returns the entire
household — including rooms that missed the multicast probe.

### Control
Plain UPnP SOAP over HTTP to port 1400. No cloud account, no Sonos API key, works fully offline.

| Service | Control path | Used for |
|---|---|---|
| `AVTransport:1` | `/MediaRenderer/AVTransport/Control` | play, pause, seek, queue edits, grouping |
| `RenderingControl:1` | `/MediaRenderer/RenderingControl/Control` | per-room volume, mute |
| `ZoneGroupTopology:1` | `/ZoneGroupTopology/Control` | rooms and groups |
| `ContentDirectory:1` | `/MediaServer/ContentDirectory/Control` | reading the queue (`Browse` of `Q:0`) |

### Streaming without an account of our own
The players already hold a Spotify login — the one linked in the Sonos app — and everything
about playback rides on that. A Spotify track goes into the ordinary queue as

```
x-sonos-spotify:spotify%3atrack%3a<id>?sid=9&flags=8224&sn=1
```

with DIDL-Lite naming the service token in a `<desc id="cdudn">` element
(`SA_RINCON2311_X_#Svc2311-0-Token`). An album or playlist goes in as one
`x-rincon-cpcontainer:` URI and the player expands it into entries itself, so a 60-track playlist
is one SOAP call rather than sixty.

The three numbers in that URI are read off the household rather than hard-coded, because `sn`
in particular says *which* linked account, and a household with two Spotify logins would play
nothing from the wrong one. There is no API that reports them: `/status/accounts` returns an
empty document on current firmware. So `Sonos/SonosSpotify.cs` reads them back off content the
Sonos app already wrote — a Spotify favourite first (its nested `r:resMD` carries the token
verbatim), then the tracks inside a saved queue. The result is cached in `settings.json`. Failing
both, it derives the token as `sid * 256 + 7` and falls back to sid 9, which is what Sonos has
used for Spotify for years.

**Search is the one thing this cannot borrow.** Every player-mediated route into a music
service's catalogue is closed on current firmware — `GetSessionId` faults 806, ContentDirectory
has no music-service container, `/status/accounts` is empty — so free-text search has to go to
Spotify's own Web API, which means a client id and a sign-in. Everything that does not need
search avoids it entirely: favourites and saved queues are plain `Browse` calls, and a pasted
link already contains the id.

#### Why Spotify gets search and YouTube Music does not
Because Spotify's Sonos URI embeds a **public** id — `spotify:track:<id>` — which anything can
build offline from a search result or a share link. YouTube Music's does not:

```
x-sonosapi-hls-static:ALkSOiGVXoKizxL077fCt60gXFtPv_k6hpKxLNxI8FADi1fN?sid=284&flags=8&sn=5
```

That token is issued by YouTube Music's SMAPI endpoint against the household's linked account.
It is not a YouTube video id, it is not derivable from a `music.youtube.com` link, and the three
ways to obtain one are all shut on current firmware (all three verified against a live
household). So for YouTube Music there is no search and no paste box; there is no honest way to
build one without the household's own service credentials.

What still reaches it is anything the players already hold a signed URI for. Favourites are one
route. Saved queues are the other, and opening one lists its tracks with their own URIs and
metadata intact — `SonosPlaylists.ReadEntriesAsync` re-wraps each `<item>` back into a DIDL
document rather than rebuilding it, so nothing about the service has to be understood to put a
track back in a queue. That is deliberately service-blind: it works the same for Apple Music,
Tidal or anything else linked to the household.

### Live updates (GENA)
`Sonos/GenaSubscriber.cs` SUBSCRIBEs to the players' event services with a `CALLBACK` pointing at
the embedded HTTP server, which answers the resulting `NOTIFY` posts on `/gena/{token}`.
Subscriptions are renewed on a timer and released on exit, so a player is not left retrying
NOTIFYs at a stranded address.

| Service | Subscribed on | Gives us |
|---|---|---|
| `Queue:1` | group coordinator | any queue edit, including ones made in the Sonos app |
| `AVTransport:1` | group coordinator | transport state, current track, duration |
| `RenderingControl:1` | selected room | volume and mute, live while you drag the Sonos app slider |

AVTransport and RenderingControl nest a second, XML-escaped document inside a `LastChange`
property — `Sonos/GenaEvents.cs` unwraps both layers. Queue events are not parsed at all: any
notification on that subscription means the queue changed, so it is simply re-read. The amber dot
in the status bar shows whether eventing is actually established.

The queue lives **on the player**, not in this app. It is read with `Browse` on object `Q:0`,
paged 100 entries at a time, and edited with `AddURIToQueue`, `RemoveTrackFromQueue` and
`ReorderTracksInQueue` (all 1-based). Jumping to an entry is `Seek` with unit `TRACK_NR`.

`Play` issued immediately after `SetAVTransportURI` races the player's state machine and returns
UPnP 701; `PlayWithRetryAsync` waits and retries. A 701 or 800 during playback usually means the
topology went stale (the household was regrouped elsewhere), so that case re-reads the topology
and retries once.

Grouping is just transport URIs: a follower is sent `x-rincon:<coordinator-uuid>`;
`BecomeCoordinatorOfStandaloneGroup` breaks it out again.

### Serving files
Sonos players cannot read your disk, so the app runs its own HTTP server
(`Serving/HttpMediaServer.cs`) on port 8099 (or the next free port) and hands the speaker URLs
like `http://192.168.1.50:8099/media/a3f1c2…/track.mp3`.

- Built on `TcpListener`, not `HttpListener`, because `HttpListener` requires an
  administrator-created URL ACL for any non-localhost prefix.
- **Tokens are a hash of the file path, not random, and the port is persisted.** A Sonos queue
  lives on the player and survives restarts of this app, so the URLs in it must keep resolving
  across sessions — random tokens meant every entry became a 404 the moment you relaunched, and
  the player would simply refuse to start with no visible error. The cached library is
  re-registered at startup for the same reason. If the PC's IP has changed since the queue was
  built, `RelinkQueueIfStale` rewrites the entries to the current address on first load.
- Byte-range requests are implemented (`206 Partial Content`), which is what Sonos uses to seek.
- Paths are never taken from the URL — the URL carries an opaque token that maps to a registered
  file, so nothing outside the library is reachable.
- NAS files are streamed *through* this server, so the speakers never need share credentials.

### Audio streaming
`Audio/LoopbackStreamer.cs` takes any `IAudioCaptureSource` → PCM16 44.1 kHz stereo (resampled
with the managed WDL resampler when needed) → LAME MP3 → fanned out to connected players by
`AudioBroadcaster`. There are two sources:

**`DeviceLoopbackSource`** — WASAPI loopback on a render endpoint. Everything the PC plays.

**`ProcessLoopbackSource`** — one application, via the Windows *Application Loopback* API:
`ActivateAudioInterfaceAsync` against the virtual device `VAD\Process_Loopback`, with
`AUDIOCLIENT_ACTIVATION_PARAMS` naming the target process id. NAudio does not wrap this, so the
COM interop is hand-written. Three consequences:

- **Windows build 20348 or newer** (Windows 11 in practice). On older builds the per-window
  entries are hidden, and if activation fails anyway the app falls back to whole-desktop capture
  and says so.
- **`INCLUDE_TARGET_PROCESS_TREE` is used deliberately.** Browsers do not render audio in the
  process that owns the window — Chrome and Edge hand it to a separate audio-service child. Tree
  mode picks up children, so targeting the browser window works. The trade-off is that you get
  *every tab* of that browser, not one tab.
- The virtual device has no mix format to query, so the capture format is fixed at 16-bit
  44.1 kHz stereo — which is exactly what the encoder wants, so nothing is resampled.

The activation call runs on a dedicated MTA thread: it completes on an MTA thread-pool thread,
and blocking the WPF STA thread waiting for it would deadlock.

### Changing source without dropping the stream
The device and its conversion chain live in a private `CaptureStage`; the encoder, the sink and the
pump thread do not. Switching source builds a new stage, swaps a `volatile` field and disposes the
old one, so **the MP3 stream is never restarted** — restarting it would close every player's
connection and Sonos would have to be told to play the URL again. The pump writes silence for any
iteration where no stage is readable, which keeps frames flowing across the swap.

If the new device cannot be opened, the old stage stays in place and the stream carries on.
Selection changes are debounced by 400 ms so arrow-keying down the dropdown does not open and close
a device per entry, and a rescan (↻) rebuilding the lists is not treated as a user choice.

> **Loopback follows the Windows volume of the endpoint it captures.** This is measured, not
> theoretical: with the desktop volume low the capture read -51 dBFS, and it tracked the volume
> slider. Set the captured device to 100% and use the Sonos room volume instead, or raise the
> **Gain** slider (applied in 32-bit float before the 16-bit conversion, so it costs no real
> quality). Muting that endpoint captures digital silence.
>
> The same holds for muting: whatever is muted is captured as silence, so "silent here, loud on
> Sonos" cannot be done by muting. Moving the application to another output is the way — see
> below.

### Sending an application to another output
Confirmed to work: point an application at an output device nothing is plugged into, then capture
that device. The PC stays quiet, Sonos gets the audio, and nothing is muted anywhere.

`Audio/AppAudioRouter.cs` does from the app what *Settings → System → Sound → Volume mixer* does
by hand. That page is backed by `Windows.Media.Internal.AudioPolicyConfig`, which is undocumented
and not projected into .NET, so:

- The activation factory is obtained with `RoGetActivationFactory` and its vtable is called
  directly (`Marshal.ReadIntPtr` + `GetDelegateForFunctionPointer`). The WinRT marshalling
  attributes other projects use for this — `UnmanagedType.HString`, `UnmanagedType.IInspectable` —
  were removed from .NET 5, so they are not an option here.
- `SetPersistedDefaultAudioEndpoint` sits at vtable slot 25 (3 `IUnknown` + 3 `IInspectable` + 19
  methods this app never calls). The layout has been stable since Windows 10 1803; only the
  interface id changed in Windows 11, so both ids are tried.
- The device id is wrapped as `\\?\SWD#MMDEVAPI#<id>#{e6327cad-…}`, and set for both the
  *multimedia* and *console* roles. A null string means "back to the default device".
- The whole process tree is moved, for the same reason process loopback needs tree mode: browsers
  render audio from a child process. New children are picked up on the poll tick.

Windows **persists** this setting, so it has to be undone. It is reverted at four points, in
descending order of how much is still working:

1. when streaming stops, or the source is switched to something else — first thing in the stop
   sequence, before the player is even told, and read back afterwards to confirm it took. If the
   app was moved *by hand* in Volume Mixer rather than from here, an override pointing at the
   streaming device is cleared too, so stopping always hands the sound back;
2. when the window closes, via the view model's `Dispose`;
3. on `SessionEnding` (log off, shut down) and on `AppDomain.ProcessExit`, which covers exits that
   never reach the window's `Closed` event;
4. failing all of that, from `PendingRoutes` in `settings.json` on the next start — the list is
   written while streaming precisely so a crash or a kill can be repaired.

The *choice* is remembered rather than the state: the output to send an app to (`RouteDeviceId`)
and the last source streamed (by process **name**, since process ids do not survive a restart) are
both restored on the next run, so the same setup is one click away. Nothing starts by itself.


Two more details that matter:
- WASAPI loopback delivers **nothing** while the machine is silent, and a radio stream that stops
  sending bytes gets dropped. `BufferedWaveProvider.ReadFully` pads the gaps with silence and the
  pump thread paces itself against the wall clock.
- The URL uses the `x-rincon-mp3radio://` scheme, which tells Sonos to treat it as a live stream
  (no duration, no seeking, automatic reconnect) rather than a file.

### Latency
Three things sit between clicking **Start** and hearing sound, and only the first two are ours:

| | |
|---|---|
| Priming | The encoder has to produce something before the URL is worth handing over. It waits for 12 KB — about half a second at 192 kbps — and gives up waiting after 2 s. |
| `SetAVTransportURI` + `Play` | Two SOAP round trips on the LAN. `PlayWithRetryAsync` absorbs the "transition not available" fault a quick `Play` can raise, so there is no fixed delay before it. |
| The player's own buffer | Sonos fills a decoder buffer before it makes a sound. `AudioBroadcaster` answers a new connection with a **burst** of the last 24 KB, frame-aligned, so that buffer fills at once instead of in real time. |

The burst size is the interesting knob and it cuts both ways: every byte of it is audio the
speaker plays *late*, so a bigger burst starts sooner but leaves the room further behind the
desktop. 24 KB — roughly a second at 192 kbps — is the compromise. Lowering the bitrate makes the
same burst cover less time and start faster still.

**Steady-state latency is roughly 1–2 seconds.** Most of it is the players' jitter buffer, which
is not tunable from outside. Fine for music and background audio; *not* usable for gaming or
lip-synced video. For tight sync the alternatives are a physical line-in on a Sonos Amp/Port/Five,
or AirPlay 2 on a supported speaker.

---

## Firewall

Windows Firewall will prompt on first run. Allow the app on **Private** networks. If you dismissed
the prompt, the symptoms are: discovery finds nothing, or speakers accept the play command and
then go silent (UPnP error 716 — the player could not fetch the URL). Fix with an elevated
PowerShell:

```powershell
New-NetFirewallRule -DisplayName "DesktopSonos" -Direction Inbound -Program "C:\path\to\DesktopSonos.exe" -Action Allow -Profile Private
```

Also check that the network profile really is Private, and that the PC and the speakers are on
the same subnet/VLAN — Sonos discovery does not cross a router, and many mesh/guest networks
block multicast.

---

## Known limits / next steps

Deliberately not in this version:

- **Position is still polled** (once a second) because no event reports it, plus a full reconcile
  every ten seconds in case the callbacks are being firewalled. Everything else is evented.
- **Enqueuing is one SOAP round trip per track**, capped at 500. Bulk loads and shuffle are
  therefore slow on big queues; `AddMultipleURIsToQueue` would be the faster path.
- **Shuffle rebuilds the queue**, which interrupts playback — it restarts from the first shuffled
  track. Sonos' own `SHUFFLE` play mode would avoid that but leaves the displayed order lying
  about what will play next, which is worse.
- **No album art**; DIDL-Lite has an `upnp:albumArtURI` slot and the media server could serve
  embedded art from TagLib. Spotify hands us art URLs already and they are thrown away.
- **Search is Spotify-only.** Favourites and saved queues cover every service the household has
  linked, but search, the account lists and the paste box are Spotify-specific, because Spotify
  is the one whose Sonos URI carries a public id. See above.
- **Free-text search needs a Spotify client id**, for the reason set out above. There is no way
  to borrow the household's own credentials on current firmware.
- **Nothing can be favourited from here.** Favourites are created in the Sonos app; this app only
  reads them. `CreateObject` on the players' ContentDirectory would be the path.
- **A pasted link shows as "Spotify album" until it is queued**, because reading its real title
  would take the very API call the paste path exists to avoid.
- **Shortcut favourites are not listed.** A few Sonos Radio tiles ("Discover Sonos Radio",
  "Sonos Presents", "Trending Now") are `r:type="shortcut"` with an empty `<res>`: they open a
  browse container in the Sonos app rather than playing anything, so there is nothing to queue.
  Reaching them would mean browsing music services, which is the thing current firmware refuses.
- **A station cannot be queued**, only played on its own with **Play now**. That is a Sonos
  constraint — a live stream is a transport URI, not a queue entry — and the app says so rather
  than dropping the row silently.

---

## Layout

```
src/DesktopSonos/
  Sonos/
    SsdpDiscovery.cs     multicast M-SEARCH on every interface
    SonosDevice.cs       one player + every UPnP action used
    SonosSoap.cs         SOAP envelope, invoke, UPnP fault decoding
    ZoneTopology.cs      GetZoneGroupState -> rooms and groups
    SonosQueue.cs        Browse "Q:0" -> the player's queue
    SonosPlaylists.cs    saved queues: list, save, delete, read the tracks inside one
    SonosFavorites.cs    Browse "FV:2" -> the household's favourites, with their own metadata
    SonosMusicServices.cs  ListAvailableServices -> sid to service name
    SonosSpotify.cs      Spotify URIs and DIDL; works out the household's sid / sn / token
    Didl.cs              DIDL-Lite metadata
  Music/
    MusicItem.cs         one row type: tracks, containers, favourites, saved queues
  Spotify/
    SpotifyAuth.cs       optional PKCE sign-in: browser + loopback callback, token refresh
    SpotifyApi.cs        Web API: search, playlists, saved albums, liked songs
    SpotifyLink.cs       parses a shared Spotify link or URI, no account needed
  Serving/
    HttpMediaServer.cs   file + live-stream HTTP server
    MediaRegistry.cs     path <-> opaque token, MIME types
    NetworkUtil.cs       interface enumeration, "which local IP reaches that speaker"
  Audio/
    LoopbackStreamer.cs      capture source -> MP3, paced in real time
    IAudioCaptureSource.cs   the capture abstraction
    DeviceLoopbackSource.cs  whole-endpoint WASAPI loopback
    ProcessLoopbackSource.cs per-application loopback (hand-written COM interop)
    AudioSourceCatalog.cs    devices + open windows, with a "making sound" marker
    ProcessTree.cs           toolhelp32 parent/child map
    AppAudioRouter.cs        move an app to another output device (undocumented COM)
    AudioBroadcaster.cs      fan-out to connected players
  Library/
    MusicLibrary.cs      folder scan + tag reading
    NetworkShare.cs      WNetAddConnection2 for NAS credentials
  UI/
    TrayIcon.cs          notification-area icon via Shell_NotifyIcon
    CenteredWrapPanel.cs wrap panel that centres each line, not just the block
  Persistence/
    AppSettings.cs       settings.json — speakers, folders, layout, streaming choices
    LibraryCache.cs      library.json — last scan, so tracks are on screen at once
    SpotifyStore.cs      spotify.json — the sign-in, refresh token encrypted with DPAPI
  ViewModels/
    MainViewModel.cs     discovery, library, playback, queue, streaming
    MusicViewModel.cs    the MUSIC tab: its lists, its five verbs, saving playlists
    SpeakerViewModel.cs  one room, its group, its volume
    QueueItemViewModel.cs one queue entry
  MainWindow.xaml        the UI
  PasswordWindow.xaml    share credential prompt
```
