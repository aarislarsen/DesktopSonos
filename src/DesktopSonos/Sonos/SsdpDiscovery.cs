using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DesktopSonos.Serving;

namespace DesktopSonos.Sonos;

/// <summary>SSDP multicast search for Sonos ZonePlayers.</summary>
public static class SsdpDiscovery
{
    private const string MulticastAddress = "239.255.255.250";
    private const int MulticastPort = 1900;
    private const string SearchTarget = "urn:schemas-upnp-org:device:ZonePlayer:1";

    private static readonly Regex LocationHeader =
        new(@"^LOCATION:\s*(?<url>\S+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Broadcasts M-SEARCH on every usable IPv4 interface and collects the responders.
    /// Only one player needs to answer — the topology query fills in the rest.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> FindZonePlayersAsync(
        TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new ConcurrentDictionary<string, IPAddress>();
        var tasks = new List<Task>();

        foreach (var local in NetworkUtil.GetLocalIPv4Addresses())
            tasks.Add(SearchOnInterfaceAsync(local, found, timeout, ct));

        if (tasks.Count == 0)
            tasks.Add(SearchOnInterfaceAsync(IPAddress.Any, found, timeout, ct));

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        return found.Values.ToList();
    }

    private static async Task SearchOnInterfaceAsync(
        IPAddress localAddress,
        ConcurrentDictionary<string, IPAddress> found,
        TimeSpan timeout,
        CancellationToken ct)
    {
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(localAddress, 0));
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Ttl = 4;
            udp.MulticastLoopback = false;

            var request = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                $"HOST: {MulticastAddress}:{MulticastPort}\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 1\r\n" +
                $"ST: {SearchTarget}\r\n" +
                "\r\n");

            var target = new IPEndPoint(IPAddress.Parse(MulticastAddress), MulticastPort);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            // Multicast is lossy; send the probe a few times.
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 3 && !cts.IsCancellationRequested; i++)
                {
                    try { await udp.SendAsync(request, request.Length, target).ConfigureAwait(false); }
                    catch { return; }
                    try { await Task.Delay(250, cts.Token).ConfigureAwait(false); }
                    catch { return; }
                }
            }, cts.Token);

            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { break; }

                var text = Encoding.ASCII.GetString(result.Buffer);
                if (text.IndexOf("ZonePlayer", StringComparison.OrdinalIgnoreCase) < 0 &&
                    text.IndexOf("Sonos", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var ip = result.RemoteEndPoint.Address;
                var match = LocationHeader.Match(text);
                if (match.Success && Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var url) &&
                    IPAddress.TryParse(url.Host, out var parsed))
                    ip = parsed;

                found.TryAdd(ip.ToString(), ip);
            }
        }
        catch (SocketException)
        {
            // Interface not usable for multicast — ignore and let the others report.
        }
        finally
        {
            udp?.Dispose();
        }
    }
}
