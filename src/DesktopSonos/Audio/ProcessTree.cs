using System.Runtime.InteropServices;

namespace DesktopSonos.Audio;

/// <summary>
/// Parent/child process relationships, needed because the process that owns a window is often
/// not the process that renders its audio (browsers use a separate audio-service child).
/// </summary>
public static class ProcessTree
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Maps every running process id to its parent's id.</summary>
    public static Dictionary<uint, uint> GetParentMap()
    {
        var map = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandle) return map;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return map;

            do
            {
                map[entry.ProcessId] = entry.ParentProcessId;
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return map;
    }

    /// <summary>The given process plus every descendant of it.</summary>
    public static HashSet<uint> GetProcessAndDescendants(uint rootProcessId,
        Dictionary<uint, uint>? parentMap = null)
    {
        parentMap ??= GetParentMap();

        // Invert into parent -> children so the walk is linear rather than quadratic.
        var children = new Dictionary<uint, List<uint>>();
        foreach (var (child, parent) in parentMap)
        {
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = new List<uint>();
            list.Add(child);
        }

        var result = new HashSet<uint> { rootProcessId };
        var pending = new Queue<uint>();
        pending.Enqueue(rootProcessId);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!children.TryGetValue(current, out var list)) continue;

            foreach (var child in list)
            {
                // Process ids get recycled, so a malformed tree could otherwise loop forever.
                if (child == current) continue;
                if (result.Add(child)) pending.Enqueue(child);
            }
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
