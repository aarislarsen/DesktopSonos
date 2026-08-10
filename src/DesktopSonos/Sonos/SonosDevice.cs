using System.Net;
using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>A single Sonos ZonePlayer and the UPnP actions we care about.</summary>
public sealed class SonosDevice
{
    public const string AvTransportService = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string AvTransportControl = "/MediaRenderer/AVTransport/Control";
    public const string RenderingService = "urn:schemas-upnp-org:service:RenderingControl:1";
    public const string RenderingControl = "/MediaRenderer/RenderingControl/Control";
    public const string GroupRenderingService = "urn:schemas-upnp-org:service:GroupRenderingControl:1";
    public const string GroupRenderingControl = "/MediaRenderer/GroupRenderingControl/Control";
    public const string TopologyService = "urn:schemas-upnp-org:service:ZoneGroupTopology:1";
    public const string TopologyControl = "/ZoneGroupTopology/Control";
    public const string DevicePropsService = "urn:schemas-upnp-org:service:DeviceProperties:1";
    public const string DevicePropsControl = "/DeviceProperties/Control";
    public const string ContentDirectoryService = "urn:schemas-upnp-org:service:ContentDirectory:1";
    public const string ContentDirectoryControl = "/MediaServer/ContentDirectory/Control";

    public SonosDevice(IPAddress ip, string uuid, string roomName, string modelName = "")
    {
        Ip = ip;
        Uuid = uuid;
        RoomName = roomName;
        ModelName = modelName;
    }

    public IPAddress Ip { get; }
    /// <summary>e.g. RINCON_949F3E1C5A2801400 (no "uuid:" prefix).</summary>
    public string Uuid { get; }
    public string RoomName { get; set; }
    public string ModelName { get; set; }
    public string Host => Ip.ToString();

    public override string ToString() => $"{RoomName} ({Ip})";

    // ---------------------------------------------------------------- description

    /// <summary>Reads /xml/device_description.xml to identify a player found via SSDP.</summary>
    public static async Task<SonosDevice?> LoadAsync(IPAddress ip, CancellationToken ct = default)
    {
        try
        {
            var url = $"http://{ip}:{SonosSoap.SonosPort}/xml/device_description.xml";
            var xml = await SonosSoap.Client.GetStringAsync(url, ct).ConfigureAwait(false);
            var doc = XDocument.Parse(xml);

            var device = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "device");
            if (device is null) return null;

            string Val(string local) =>
                device.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value ?? string.Empty;

            var udn = Val("UDN");
            if (udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase)) udn = udn[5..];
            if (string.IsNullOrWhiteSpace(udn)) return null;

            var room = Val("roomName");
            if (string.IsNullOrWhiteSpace(room)) room = Val("friendlyName");

            return new SonosDevice(ip, udn, room, Val("modelName"));
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- transport

    private Task<Dictionary<string, string>> AvAsync(string action, CancellationToken ct,
        params (string Key, string Value)[] args) =>
        SonosSoap.InvokeAsync(Host, AvTransportControl, AvTransportService, action,
            args.Select(a => new KeyValuePair<string, string>(a.Key, a.Value)), ct);

    public Task PlayAsync(CancellationToken ct = default) =>
        AvAsync("Play", ct, ("InstanceID", "0"), ("Speed", "1"));

    /// <summary>
    /// Play issued straight after SetAVTransportURI races the player's own state machine and
    /// comes back as UPnP 701. Give it a few hundred milliseconds and try again before failing.
    /// </summary>
    public async Task PlayWithRetryAsync(int attempts = 4, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await PlayAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (SonosException ex) when (ex.ErrorCode == 701 && attempt < attempts)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Number of tracks the player currently has loaded (0 means an empty queue).</summary>
    public async Task<int> GetLoadedTrackCountAsync(CancellationToken ct = default)
    {
        var info = await GetMediaInfoAsync(ct).ConfigureAwait(false);
        return info.TryGetValue("NrTracks", out var text) && int.TryParse(text, out var count)
            ? count
            : 0;
    }

    public Task PauseAsync(CancellationToken ct = default) =>
        AvAsync("Pause", ct, ("InstanceID", "0"));

    public Task StopAsync(CancellationToken ct = default) =>
        AvAsync("Stop", ct, ("InstanceID", "0"));

    public Task NextAsync(CancellationToken ct = default) =>
        AvAsync("Next", ct, ("InstanceID", "0"));

    public Task PreviousAsync(CancellationToken ct = default) =>
        AvAsync("Previous", ct, ("InstanceID", "0"));

    /// <summary>unit is REL_TIME ("0:01:23"), TRACK_NR ("3") or TIME_DELTA.</summary>
    public Task SeekAsync(string unit, string target, CancellationToken ct = default) =>
        AvAsync("Seek", ct, ("InstanceID", "0"), ("Unit", unit), ("Target", target));

    /// <summary>
    /// Loading a live stream makes the player fetch the URL before it answers, so this gets a
    /// much longer deadline than an ordinary command.
    /// </summary>
    public Task SetAvTransportUriAsync(string uri, string metadata, CancellationToken ct = default)
    {
        var isStream = uri.StartsWith("x-rincon-mp3radio:", StringComparison.OrdinalIgnoreCase) ||
                       uri.StartsWith("x-rinconurischeme:", StringComparison.OrdinalIgnoreCase);

        return SonosSoap.InvokeAsync(Host, AvTransportControl, AvTransportService, "SetAVTransportURI",
            new[]
            {
                new KeyValuePair<string, string>("InstanceID", "0"),
                new KeyValuePair<string, string>("CurrentURI", uri),
                new KeyValuePair<string, string>("CurrentURIMetaData", metadata)
            },
            ct,
            isStream ? SonosSoap.StreamLoadTimeout : null);
    }

    public Task<Dictionary<string, string>> GetTransportInfoAsync(CancellationToken ct = default) =>
        AvAsync("GetTransportInfo", ct, ("InstanceID", "0"));

    public Task<Dictionary<string, string>> GetPositionInfoAsync(CancellationToken ct = default) =>
        AvAsync("GetPositionInfo", ct, ("InstanceID", "0"));

    public Task<Dictionary<string, string>> GetMediaInfoAsync(CancellationToken ct = default) =>
        AvAsync("GetMediaInfo", ct, ("InstanceID", "0"));

    /// <summary>NORMAL, REPEAT_ALL, SHUFFLE, SHUFFLE_NOREPEAT, REPEAT_ONE.</summary>
    public Task SetPlayModeAsync(string mode, CancellationToken ct = default) =>
        AvAsync("SetPlayMode", ct, ("InstanceID", "0"), ("NewPlayMode", mode));

    public async Task<string> GetTransportStateAsync(CancellationToken ct = default)
    {
        var r = await GetTransportInfoAsync(ct).ConfigureAwait(false);
        return r.TryGetValue("CurrentTransportState", out var s) ? s : "UNKNOWN";
    }

    // ---------------------------------------------------------------- queue

    public Task ClearQueueAsync(CancellationToken ct = default) =>
        AvAsync("RemoveAllTracksFromQueue", ct, ("InstanceID", "0"));

    /// <summary>Appends (or inserts next) a track. Returns the resulting queue length.</summary>
    public async Task<int> AddUriToQueueAsync(string uri, string metadata, int desiredFirstTrackNumber = 0,
        bool asNext = false, CancellationToken ct = default)
    {
        var r = await AvAsync("AddURIToQueue", ct,
            ("InstanceID", "0"),
            ("EnqueuedURI", uri),
            ("EnqueuedURIMetaData", metadata),
            ("DesiredFirstTrackNumberEnqueued", desiredFirstTrackNumber.ToString()),
            ("EnqueueAsNext", asNext ? "1" : "0")).ConfigureAwait(false);
        return r.TryGetValue("NewQueueLength", out var n) && int.TryParse(n, out var len) ? len : 0;
    }

    /// <summary>Points the player at its own queue (x-rincon-queue:UUID#0).</summary>
    public Task PlayFromQueueAsync(CancellationToken ct = default) =>
        SetAvTransportUriAsync($"x-rincon-queue:{Uuid}#0", string.Empty, ct);

    /// <summary>True when the transport is pointed at the queue rather than a stream or line-in.</summary>
    public async Task<bool> IsPlayingFromQueueAsync(CancellationToken ct = default)
    {
        var info = await GetMediaInfoAsync(ct).ConfigureAwait(false);
        var uri = info.GetValueOrDefault("CurrentURI") ?? string.Empty;
        return uri.StartsWith("x-rincon-queue:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Removes one entry. objectId looks like "Q:0/7" and is 1-based.</summary>
    public Task RemoveTrackFromQueueAsync(string objectId, CancellationToken ct = default) =>
        AvAsync("RemoveTrackFromQueue", ct,
            ("InstanceID", "0"), ("ObjectID", objectId), ("UpdateID", "0"));

    /// <summary>Moves entries within the queue. All indices are 1-based.</summary>
    public Task ReorderTracksInQueueAsync(int startingIndex, int numberOfTracks, int insertBefore,
        CancellationToken ct = default) =>
        AvAsync("ReorderTracksInQueue", ct,
            ("InstanceID", "0"),
            ("StartingIndex", startingIndex.ToString()),
            ("NumberOfTracks", numberOfTracks.ToString()),
            ("InsertBefore", insertBefore.ToString()),
            ("UpdateID", "0"));

    /// <summary>
    /// Writes the current queue to the household as a saved queue ("Sonos playlist"). Pass an
    /// empty <paramref name="objectId"/> to create one; passing an existing "SQ:n" overwrites it.
    /// Returns the assigned object id.
    /// </summary>
    public async Task<string> SaveQueueAsync(string title, string objectId = "",
        CancellationToken ct = default)
    {
        var r = await AvAsync("SaveQueue", ct,
            ("InstanceID", "0"), ("Title", title), ("ObjectID", objectId)).ConfigureAwait(false);
        return r.GetValueOrDefault("AssignedObjectID") ?? "";
    }

    /// <summary>Deletes a saved queue. ObjectID looks like "SQ:3".</summary>
    public Task DestroyObjectAsync(string objectId, CancellationToken ct = default) =>
        SonosSoap.InvokeAsync(Host, ContentDirectoryControl, ContentDirectoryService, "DestroyObject",
            new[] { new KeyValuePair<string, string>("ObjectID", objectId) }, ct);

    /// <summary>
    /// ContentDirectory Browse. "Q:0" is the player's own queue; the returned Result is a
    /// DIDL-Lite document (already unescaped by the SOAP layer).
    /// </summary>
    public async Task<(string Result, int NumberReturned, int TotalMatches)> BrowseAsync(
        string objectId, int startingIndex, int requestedCount, CancellationToken ct = default)
    {
        var response = await SonosSoap.InvokeAsync(Host, ContentDirectoryControl, ContentDirectoryService,
            "Browse",
            new[]
            {
                new KeyValuePair<string, string>("ObjectID", objectId),
                new KeyValuePair<string, string>("BrowseFlag", "BrowseDirectChildren"),
                new KeyValuePair<string, string>("Filter", "*"),
                new KeyValuePair<string, string>("StartingIndex", startingIndex.ToString()),
                new KeyValuePair<string, string>("RequestedCount", requestedCount.ToString()),
                new KeyValuePair<string, string>("SortCriteria", string.Empty)
            }, ct).ConfigureAwait(false);

        var result = response.GetValueOrDefault("Result") ?? string.Empty;
        int.TryParse(response.GetValueOrDefault("NumberReturned"), out var returned);
        int.TryParse(response.GetValueOrDefault("TotalMatches"), out var total);
        return (result, returned, total);
    }

    // ---------------------------------------------------------------- volume

    public async Task<int> GetVolumeAsync(CancellationToken ct = default)
    {
        var r = await SonosSoap.InvokeAsync(Host, RenderingControl, RenderingService, "GetVolume",
            new[]
            {
                new KeyValuePair<string, string>("InstanceID", "0"),
                new KeyValuePair<string, string>("Channel", "Master")
            }, ct).ConfigureAwait(false);
        return r.TryGetValue("CurrentVolume", out var v) && int.TryParse(v, out var vol) ? vol : 0;
    }

    public Task SetVolumeAsync(int volume, CancellationToken ct = default) =>
        SonosSoap.InvokeAsync(Host, RenderingControl, RenderingService, "SetVolume",
            new[]
            {
                new KeyValuePair<string, string>("InstanceID", "0"),
                new KeyValuePair<string, string>("Channel", "Master"),
                new KeyValuePair<string, string>("DesiredVolume", Math.Clamp(volume, 0, 100).ToString())
            }, ct);

    public async Task<bool> GetMuteAsync(CancellationToken ct = default)
    {
        var r = await SonosSoap.InvokeAsync(Host, RenderingControl, RenderingService, "GetMute",
            new[]
            {
                new KeyValuePair<string, string>("InstanceID", "0"),
                new KeyValuePair<string, string>("Channel", "Master")
            }, ct).ConfigureAwait(false);
        return r.TryGetValue("CurrentMute", out var m) && m == "1";
    }

    public Task SetMuteAsync(bool mute, CancellationToken ct = default) =>
        SonosSoap.InvokeAsync(Host, RenderingControl, RenderingService, "SetMute",
            new[]
            {
                new KeyValuePair<string, string>("InstanceID", "0"),
                new KeyValuePair<string, string>("Channel", "Master"),
                new KeyValuePair<string, string>("DesiredMute", mute ? "1" : "0")
            }, ct);

    /// <summary>Sets the volume of the whole group, preserving the relative balance.</summary>
    public Task SetGroupVolumeAsync(int volume, CancellationToken ct = default) =>
        SonosSoap.InvokeAsync(Host, GroupRenderingControl, GroupRenderingService, "SetGroupVolume",
            new[]
            {
                new KeyValuePair<string, string>("InstanceID", "0"),
                new KeyValuePair<string, string>("DesiredVolume", Math.Clamp(volume, 0, 100).ToString())
            }, ct);

    // ---------------------------------------------------------------- grouping

    /// <summary>Makes this player follow <paramref name="coordinatorUuid"/>.</summary>
    public Task JoinAsync(string coordinatorUuid, CancellationToken ct = default) =>
        SetAvTransportUriAsync($"x-rincon:{coordinatorUuid}", string.Empty, ct);

    public Task LeaveGroupAsync(CancellationToken ct = default) =>
        AvAsync("BecomeCoordinatorOfStandaloneGroup", ct, ("InstanceID", "0"));

    public async Task<string> GetZoneGroupStateAsync(CancellationToken ct = default)
    {
        var r = await SonosSoap.InvokeAsync(Host, TopologyControl, TopologyService,
            "GetZoneGroupState", null, ct).ConfigureAwait(false);
        return r.TryGetValue("ZoneGroupState", out var s) ? s : string.Empty;
    }
}
