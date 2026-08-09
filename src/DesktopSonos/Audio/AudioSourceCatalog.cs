using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DesktopSonos.Audio;

public enum AudioSourceKind
{
    /// <summary>Everything mixed to a render endpoint.</summary>
    Device,
    /// <summary>One application window's process tree.</summary>
    Process
}

public sealed class AudioSourceOption
{
    public AudioSourceKind Kind { get; init; }

    /// <summary>Endpoint id for <see cref="AudioSourceKind.Device"/>; empty means the default.</summary>
    public string DeviceId { get; init; } = "";

    public uint ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>True when the process tree currently has a live mixer session.</summary>
    public bool IsAudible { get; init; }

    public string Display => Kind == AudioSourceKind.Device
        ? Title
        : $"{(IsAudible ? "♪ " : "   ")}{ProcessName} — {Title}";

    public override string ToString() => Display;
}

/// <summary>An output device an application can be sent to before it is captured.</summary>
public sealed class RenderDeviceOption
{
    /// <summary>Empty means "leave the app where it is".</summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>True for the device Windows currently plays everything through.</summary>
    public bool IsDefault { get; init; }

    public override string ToString() => Name;
}

/// <summary>Builds the list of things the user can pick as a stream source.</summary>
public static class AudioSourceCatalog
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const uint GwOwner = 4;

    public static List<AudioSourceOption> Build()
    {
        var options = new List<AudioSourceOption>();
        options.AddRange(GetDeviceOptions());

        if (ProcessLoopbackSource.IsSupported)
            options.AddRange(GetWindowOptions());

        return options;
    }

    /// <summary>
    /// Every active output, for the "send the app to" list. The device the PC is actually
    /// listening on is a poor choice there, so it is marked and listed last.
    /// </summary>
    public static List<RenderDeviceOption> BuildRenderDevices()
    {
        var results = new List<RenderDeviceOption>
        {
            new() { Id = "", Name = "Leave it where it is" }
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            var defaultId = "";
            try { defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
            catch { }

            var devices = enumerator
                .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new RenderDeviceOption
                {
                    Id = device.ID,
                    Name = device.ID == defaultId
                        ? $"{device.FriendlyName} (this PC's speakers)"
                        : device.FriendlyName,
                    IsDefault = device.ID == defaultId
                })
                .OrderBy(option => option.IsDefault)
                .ThenBy(option => option.Name, StringComparer.OrdinalIgnoreCase);

            results.AddRange(devices);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Enumerating render devices failed: {ex.Message}");
        }

        return results;
    }

    private static IEnumerable<AudioSourceOption> GetDeviceOptions()
    {
        var results = new List<AudioSourceOption>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            results.Add(new AudioSourceOption
            {
                Kind = AudioSourceKind.Device,
                DeviceId = "",
                Title = $"Entire desktop — {defaultDevice.FriendlyName}"
            });

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (device.ID == defaultDevice.ID) continue;
                results.Add(new AudioSourceOption
                {
                    Kind = AudioSourceKind.Device,
                    DeviceId = device.ID,
                    Title = $"All sound from {device.FriendlyName}"
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Enumerating render devices failed: {ex.Message}");
        }
        return results;
    }

    private static IEnumerable<AudioSourceOption> GetWindowOptions()
    {
        var parentMap = ProcessTree.GetParentMap();
        var audibleProcessIds = GetAudibleProcessIds();
        var ownProcessId = (uint)Environment.ProcessId;

        // One entry per process, keyed by the first (topmost) window we see for it.
        var byProcess = new Dictionary<uint, (string Title, string Name)>();

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            if (GetWindow(handle, GwOwner) != IntPtr.Zero) return true;
            if ((GetWindowLong(handle, GwlExStyle) & WsExToolWindow) != 0) return true;

            var length = GetWindowTextLength(handle);
            if (length <= 0) return true;

            var builder = new StringBuilder(length + 1);
            GetWindowText(handle, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (title.Length == 0) return true;

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0 || processId == ownProcessId) return true;
            if (byProcess.ContainsKey(processId)) return true;

            string name;
            try { name = Process.GetProcessById((int)processId).ProcessName; }
            catch { return true; }

            byProcess[processId] = (title, name);
            return true;
        }, IntPtr.Zero);

        var results = new List<AudioSourceOption>();
        foreach (var (processId, info) in byProcess)
        {
            var family = ProcessTree.GetProcessAndDescendants(processId, parentMap);
            results.Add(new AudioSourceOption
            {
                Kind = AudioSourceKind.Process,
                ProcessId = processId,
                ProcessName = info.Name,
                Title = Shorten(info.Title, 70),
                IsAudible = family.Overlaps(audibleProcessIds)
            });
        }

        // Anything currently making noise is what the user is most likely after.
        return results
            .OrderByDescending(o => o.IsAudible)
            .ThenBy(o => o.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Title, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Process ids that currently own a render session in the volume mixer.</summary>
    private static HashSet<uint> GetAudibleProcessIds()
    {
        var result = new HashSet<uint>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                    result.Add(session.GetProcessID);
                }
                catch
                {
                    // Session disappeared mid-enumeration.
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Enumerating audio sessions failed: {ex.Message}");
        }
        return result;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    // ---------------------------------------------------------------- interop

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
}
