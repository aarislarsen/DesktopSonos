# DesktopSonos
![Alt text](/image.png?raw=true "Optional Title")


A Windows desktop app (C# / WPF / .NET 8) that finds the Sonos players on your LAN and plays
audio to them from three sources:

1. **Local files** — MP3, FLAC, M4A, WAV, WMA, OGG, AIFF from any folder on the PC.
2. **NAS / network shares** — any UNC path (`\\nas\music`), with optional credentials.
3. **Live audio from this PC** — either everything the Windows mixer plays, or a single
   application (pick its window), which can be moved to another output so it plays only on Sonos.

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
| Network | The PC and the players on the same subnet. No Sonos account, no API key, no internet. |

The project targets `net8.0-windows` with `PlatformTarget=x64`. The x64 pin is not cosmetic: the
LAME encoder in `NAudio.Lame` ships architecture-specific native DLLs, and AnyCPU picks the wrong
one. NuGet packages: `NAudio`, `NAudio.Lame`, `CommunityToolkit.Mvvm`, `TagLibSharp` — all
restored automatically.

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
   path** for a NAS.
4. **To start with Windows**: press `Win+R`, run `shell:startup`, and drop a shortcut to
   `DesktopSonos.exe` in the folder that opens. It comes back in whichever shape it was closed in,
   compact strip included.

To update later: `git pull`, publish again, and copy the new `publish` folder over the old one.
Settings are kept elsewhere and are not disturbed.

Everything the app remembers lives in **`%APPDATA%\DesktopSonos`**:

| File | What it holds |
|---|---|
| `settings.json` | speakers, library folders, last room, panel widths and collapse state, window size, compact-view state, media-server port, stream bitrate and gain, output-routing choice |
| `library.json` | the scanned library, so tracks are on screen before any folder is re-read |
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

**Rooms** are on the left, the **queue** is in the middle, the **library** is on the right, and the
transport bar runs along the bottom. The two side panels collapse sideways with the ▾ / ▸ button
in their headers, which is also how you stop a stray click from changing which room is playing.

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
| **Ctrl+F**, or just start typing | search the library |
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
  embedded art from TagLib.
- **No playlists, no favorites, no Sonos music-service content.**

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
    Didl.cs              DIDL-Lite metadata
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
  ViewModels/
    MainViewModel.cs     discovery, library, playback, queue, streaming
    SpeakerViewModel.cs  one room, its group, its volume
    QueueItemViewModel.cs one queue entry
  MainWindow.xaml        the UI
  PasswordWindow.xaml    share credential prompt
```
