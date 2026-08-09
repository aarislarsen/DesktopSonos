using System.Runtime.InteropServices;

namespace DesktopSonos.Library;

/// <summary>
/// Optional credential support for NAS shares. If the signed-in Windows account already
/// has access to \\nas\music you never need this — just add the UNC path directly.
/// </summary>
public static class NetworkShare
{
    private const int ResourceTypeDisk = 0x00000001;
    private const int NoError = 0;
    private const int ErrorSessionCredentialConflict = 1219;
    private const int ErrorAlreadyAssigned = 85;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(ref NetResource netResource,
        string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    /// <summary>
    /// Authenticates to a UNC share for this logon session (no drive letter is mapped).
    /// Returns null on success, otherwise a human-readable error.
    /// </summary>
    public static string? Connect(string uncPath, string? username, string? password)
    {
        var share = GetShareRoot(uncPath);
        if (share is null) return $"\"{uncPath}\" is not a UNC path (expected \\\\server\\share).";

        var resource = new NetResource
        {
            dwType = ResourceTypeDisk,
            lpRemoteName = share
        };

        var result = WNetAddConnection2(ref resource, password, username, 0);
        return result switch
        {
            NoError => null,
            ErrorAlreadyAssigned => null,
            ErrorSessionCredentialConflict =>
                "Windows already has a connection to that server with different credentials. " +
                "Run \"net use " + share + " /delete\" and try again.",
            5 => "Access denied — check the user name and password.",
            53 => "Network path not found — check the server name.",
            67 => "Share name not found on that server.",
            86 => "The specified password is not correct.",
            _ => $"WNetAddConnection2 failed with error {result}."
        };
    }

    public static void Disconnect(string uncPath)
    {
        var share = GetShareRoot(uncPath);
        if (share is null) return;
        WNetCancelConnection2(share, 0, true);
    }

    /// <summary>\\server\share\music\rock -> \\server\share</summary>
    public static string? GetShareRoot(string uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath)) return null;
        if (!uncPath.StartsWith(@"\\")) return null;

        var parts = uncPath.TrimEnd('\\')
                           .Substring(2)
                           .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        return $@"\\{parts[0]}\{parts[1]}";
    }

    public static bool IsUnc(string path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith(@"\\");
}
