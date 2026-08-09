using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DesktopSonos.Audio;

/// <summary>
/// One process's output device, remembered so it can be put back.
/// </summary>
public sealed class RoutedProcess
{
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = "";

    /// <summary>The endpoint the process used before we moved it; empty means the system default.</summary>
    public string PreviousDeviceId { get; set; } = "";
}

/// <summary>
/// Moves an application to a different output device, which is what the "App volume and device
/// preferences" page in Windows Settings does. Sending the captured app to an output nothing is
/// plugged into is the only reliable way to hear it on Sonos and not on the PC: muting the app
/// instead would also mute what we capture, because loopback taps the audio after the mixer.
///
/// The API behind that Settings page (Windows.Media.Internal.AudioPolicyConfig) is not documented
/// and not projected into .NET, so the vtable is called directly here. Method order is the same on
/// every build since 1803; only the interface id changed in Windows 11. If activation fails the
/// caller simply carries on without routing.
///
/// Routing is persisted by Windows, so <see cref="RestoreAll"/> must run before the app exits —
/// and anything left behind by a crash is cleaned up from the settings file on the next start.
/// </summary>
public sealed class AppAudioRouter : IDisposable
{
    private const string MmDevApiToken = @"\\?\SWD#MMDEVAPI#";
    private const string DevInterfaceAudioRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    // IUnknown (3) + IInspectable (3) + 19 methods we never call.
    private const int SlotSetPersistedDefaultAudioEndpoint = 25;
    private const int SlotGetPersistedDefaultAudioEndpoint = 26;

    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;

    private static readonly Guid IidWindows11 = new("ab3d4648-e242-459f-b02f-541c70306324");
    private static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    private readonly Dictionary<uint, RoutedProcess> _moved = new();

    private IntPtr _factory;
    private uint _targetProcessId;
    private string _targetDeviceId = "";
    private bool _active;

    public event Action<string>? Log;

    /// <summary>Per-application output devices arrived in Windows 10 1803.</summary>
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 17134;

    public bool IsActive => _active;

    /// <summary>Everything currently moved, for the settings file.</summary>
    public List<RoutedProcess> Pending => _moved.Values.ToList();

    /// <summary>
    /// Sends <paramref name="rootProcessId"/> and its children to <paramref name="deviceId"/>.
    /// Children matter: browsers render audio from a separate utility process.
    /// </summary>
    public bool Route(uint rootProcessId, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        if (!EnsureFactory()) return false;

        _targetProcessId = rootProcessId;
        _targetDeviceId = deviceId;
        _active = true;

        var moved = Apply();
        if (moved > 0)
            Log?.Invoke($"Moved {moved} process(es) to the chosen output device. " +
                        "An app that was already playing may need a moment, or a restart, to follow.");
        else
            Log?.Invoke("Could not move that app to another output device — capturing it where it is.");

        return moved > 0;
    }

    /// <summary>Catches processes that started after <see cref="Route"/>, such as new browser tabs.</summary>
    public void Reapply()
    {
        if (_active) Apply();
    }

    private int Apply()
    {
        var family = ProcessTree.GetProcessAndDescendants(_targetProcessId);
        var moved = 0;

        foreach (var processId in family)
        {
            if (_moved.ContainsKey(processId)) continue;

            var previous = GetRoute(processId) ?? "";

            // Windows persists this against the *application*, not the process id, so a helper
            // process that appears after routing already reports the streaming device as its
            // own setting. Recording that as "previous" would put it straight back there on
            // stop — which is exactly how a browser ends up stuck on the spare output.
            if (string.Equals(previous, _targetDeviceId, StringComparison.OrdinalIgnoreCase))
                previous = "";

            if (!SetRoute(processId, _targetDeviceId)) continue;

            _moved[processId] = new RoutedProcess
            {
                ProcessId = processId,
                ProcessName = NameOf(processId),
                PreviousDeviceId = previous
            };
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Puts every process we moved back where it was, and reads the setting back to be sure it
    /// took. Anything that refuses is reported by name rather than failing quietly.
    /// </summary>
    public void RestoreAll()
    {
        _active = false;
        if (_moved.Count == 0) return;

        var stuck = new List<string>();

        foreach (var entry in _moved.Values)
        {
            var restored = entry.PreviousDeviceId.Length == 0
                ? RestoreToDefault(entry.ProcessId)
                : SetRoute(entry.ProcessId, entry.PreviousDeviceId);

            // Reading it back catches the case where the call reports success but the policy
            // store kept the old value — the difference between "reverted" and "looks reverted".
            if (restored && !IsRouteEqual(GetRoute(entry.ProcessId), entry.PreviousDeviceId))
            {
                restored = SetRoute(entry.ProcessId, entry.PreviousDeviceId);
                if (restored)
                    restored = IsRouteEqual(GetRoute(entry.ProcessId), entry.PreviousDeviceId);
            }

            if (!restored && IsAlive(entry.ProcessId))
                stuck.Add(entry.ProcessName.Length > 0 ? entry.ProcessName : entry.ProcessId.ToString());
        }

        Log?.Invoke($"Put {_moved.Count} process(es) back on their normal output device. " +
                    "An app that is playing right now may keep using the other output until it " +
                    "next opens the audio device — pausing and playing again is usually enough.");

        if (stuck.Count > 0)
            Log?.Invoke($"Could not put {string.Join(", ", stuck.Distinct())} back — check " +
                        "Settings › System › Sound › Volume mixer.");

        _moved.Clear();
    }

    /// <summary>
    /// Hands a process back to the system default. Removing the override on its own often leaves
    /// an application that already has the device open — a browser playing a video, say — sitting
    /// on the old output, because nothing tells it anything changed. Naming the default endpoint
    /// explicitly first is a *change* rather than a removal, which the audio engine acts on; the
    /// override is then cleared so the app follows the default from there on.
    /// </summary>
    private bool RestoreToDefault(uint processId)
    {
        var defaultId = DefaultRenderDeviceId();
        if (defaultId.Length > 0) SetRoute(processId, defaultId);

        return SetRoute(processId, "");
    }

    private static string DefaultRenderDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Both forms of "the system default" — empty and null — count as the same thing.</summary>
    private static bool IsRouteEqual(string? actual, string expected) =>
        string.Equals(actual ?? "", expected ?? "", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlive(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch
        {
            // Gone already, which is as good as restored.
            return false;
        }
    }

    /// <summary>
    /// Clears a process tree's output override when it points at <paramref name="deviceId"/>,
    /// whether or not this app is what set it. Covers the case where the user had already moved
    /// the application by hand in Volume Mixer: stopping the stream should still give the PC its
    /// sound back, and only an override onto the streaming device is touched.
    /// </summary>
    public int ClearMatchingRoutes(uint rootProcessId, string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !EnsureFactory()) return 0;

        var cleared = 0;
        foreach (var processId in ProcessTree.GetProcessAndDescendants(rootProcessId))
        {
            var current = GetRoute(processId);
            if (string.IsNullOrEmpty(current)) continue;
            if (!string.Equals(current, deviceId, StringComparison.OrdinalIgnoreCase)) continue;

            if (RestoreToDefault(processId)) cleared++;
        }

        return cleared;
    }

    /// <summary>
    /// Undoes routing left behind by a crash. The previous device is nearly always the system
    /// default, so the worst case for a recycled process id is that some app goes back to default.
    /// </summary>
    public int RecoverPending(IEnumerable<RoutedProcess> pending)
    {
        var recovered = 0;
        foreach (var entry in pending)
        {
            if (SetRoute(entry.ProcessId, entry.PreviousDeviceId)) recovered++;
        }
        return recovered;
    }

    private static string NameOf(uint processId)
    {
        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ policy calls

    /// <summary>Empty <paramref name="deviceId"/> means "use the system default again".</summary>
    private bool SetRoute(uint processId, string deviceId)
    {
        if (!EnsureFactory()) return false;

        var handle = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var full = $"{MmDevApiToken}{deviceId}{DevInterfaceAudioRender}";
                if (WindowsCreateString(full, (uint)full.Length, out handle) != 0) return false;
            }

            var set = GetMethod<SetPersistedEndpoint>(SlotSetPersistedDefaultAudioEndpoint);

            // Both roles, or the app keeps the old device for one kind of playback.
            // All three roles: leaving one behind means the app keeps the old device for that
            // kind of playback, which looks like the setting simply not working.
            var multimedia = set(_factory, processId, DataFlowRender, RoleMultimedia, handle);
            var console = set(_factory, processId, DataFlowRender, RoleConsole, handle);
            set(_factory, processId, DataFlowRender, RoleCommunications, handle);

            return multimedia >= 0 && console >= 0;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Setting the output device for process {processId} failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (handle != IntPtr.Zero) WindowsDeleteString(handle);
        }
    }

    /// <summary>The endpoint id currently forced on a process, or empty when it follows the default.</summary>
    public string? GetRoute(uint processId)
    {
        if (!EnsureFactory()) return null;

        var handle = IntPtr.Zero;
        try
        {
            var get = GetMethod<GetPersistedEndpoint>(SlotGetPersistedDefaultAudioEndpoint);
            if (get(_factory, processId, DataFlowRender, RoleMultimedia, out handle) < 0) return null;
            if (handle == IntPtr.Zero) return "";

            var buffer = WindowsGetStringRawBuffer(handle, out var length);
            var value = buffer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(buffer, (int)length) ?? "";
            return Unpack(value);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) WindowsDeleteString(handle);
        }
    }

    private static string Unpack(string deviceId)
    {
        if (deviceId.StartsWith(MmDevApiToken, StringComparison.OrdinalIgnoreCase))
            deviceId = deviceId[MmDevApiToken.Length..];
        if (deviceId.EndsWith(DevInterfaceAudioRender, StringComparison.OrdinalIgnoreCase))
            deviceId = deviceId[..^DevInterfaceAudioRender.Length];
        return deviceId;
    }

    private bool EnsureFactory()
    {
        if (_factory != IntPtr.Zero) return true;
        if (!IsSupported) return false;

        // Windows 11 renamed the interface id; the layout behind it is unchanged.
        var preferred = Environment.OSVersion.Version.Build >= 21390 ? IidWindows11 : IidDownlevel;
        var fallback = Environment.OSVersion.Version.Build >= 21390 ? IidDownlevel : IidWindows11;

        if (TryActivate(preferred) || TryActivate(fallback)) return true;

        Log?.Invoke("Windows would not hand over the per-application audio policy interface; " +
                    "route the app from Settings › System › Sound › Volume mixer instead.");
        return false;
    }

    private bool TryActivate(Guid iid)
    {
        var className = IntPtr.Zero;
        try
        {
            const string activatableClassId = "Windows.Media.Internal.AudioPolicyConfig";
            if (WindowsCreateString(activatableClassId, (uint)activatableClassId.Length, out className) != 0)
                return false;

            var hr = RoGetActivationFactory(className, ref iid, out var factory);
            if (hr < 0 || factory == IntPtr.Zero) return false;

            _factory = factory;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (className != IntPtr.Zero) WindowsDeleteString(className);
        }
    }

    /// <summary>Reads a slot out of the object's vtable and wraps it as a callable delegate.</summary>
    private T GetMethod<T>(int slot) where T : Delegate
    {
        var table = Marshal.ReadIntPtr(_factory);
        var function = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(function);
    }

    public void Dispose()
    {
        RestoreAll();

        if (_factory != IntPtr.Zero)
        {
            try { Marshal.Release(_factory); } catch { }
            _factory = IntPtr.Zero;
        }
    }

    // ------------------------------------------------------------------ interop

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedEndpoint(IntPtr self, uint processId, int flow, int role, IntPtr deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedEndpoint(IntPtr self, uint processId, int flow, int role, out IntPtr deviceId);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, uint length, out IntPtr value);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr value, out uint length);
}
