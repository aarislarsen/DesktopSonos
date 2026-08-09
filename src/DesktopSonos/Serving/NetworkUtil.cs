using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DesktopSonos.Serving;

public static class NetworkUtil
{
    /// <summary>Usable, multicast-capable IPv4 addresses on this machine.</summary>
    public static IEnumerable<IPAddress> GetLocalIPv4Addresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (!nic.SupportsMulticast) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(unicast.Address)) continue;
                // Skip APIPA addresses, they never reach the LAN.
                var bytes = unicast.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                yield return unicast.Address;
            }
        }
    }

    /// <summary>
    /// The local address the OS would use to reach <paramref name="remote"/>.
    /// This is what we must put in URLs we hand to a speaker — important on machines
    /// with several NICs, a VPN, Hyper-V or WSL virtual switches.
    /// </summary>
    public static IPAddress GetLocalAddressFor(IPAddress remote)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(remote, 9);
            if (socket.LocalEndPoint is IPEndPoint ep &&
                !IPAddress.IsLoopback(ep.Address) &&
                !Equals(ep.Address, IPAddress.Any))
                return ep.Address;
        }
        catch
        {
            // fall through
        }
        return GetLocalIPv4Addresses().FirstOrDefault() ?? IPAddress.Loopback;
    }
}
