using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using DesktopSonos.Serving;

namespace DesktopSonos.Sonos;

/// <summary>
/// UPnP eventing (GENA). We SUBSCRIBE to a player's service with a CALLBACK pointing at our own
/// embedded HTTP server; the player then POSTs a NOTIFY every time something changes, which
/// removes the need to poll for queue edits, transport state and volume.
///
/// Subscriptions expire, so they are renewed on a timer and released on shutdown — a player
/// that keeps a dead subscription will retry NOTIFYs at a stranded address for a while.
/// </summary>
public sealed class GenaSubscriber : IDisposable
{
    private const int RequestedTimeoutSeconds = 1800;

    public const string AvTransportEventPath = "/MediaRenderer/AVTransport/Event";
    public const string RenderingEventPath = "/MediaRenderer/RenderingControl/Event";
    public const string QueueEventPath = "/MediaRenderer/Queue/Event";

    private readonly HttpMediaServer _server;
    private readonly ConcurrentDictionary<string, Subscription> _byToken = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _renewTimer;

    public GenaSubscriber(HttpMediaServer server)
    {
        _server = server;
        _server.GenaNotification += OnNotification;
        _renewTimer = new Timer(_ => _ = RenewDueAsync(), null,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>Raised with (serviceName, notify body) on a background thread.</summary>
    public event Action<string, string>? Notified;

    public event Action<string>? Log;

    public bool HasSubscriptions => !_byToken.IsEmpty;

    private sealed class Subscription
    {
        public required string Token { get; init; }
        public required string ServiceName { get; init; }
        public required string Host { get; init; }
        public required string EventPath { get; init; }
        public string Sid { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
    }

    /// <summary>
    /// Drops every existing subscription and subscribes afresh: AVTransport and Queue on the
    /// group coordinator (it owns playback), RenderingControl on the individual room (volume is
    /// per-player).
    /// </summary>
    public async Task ResubscribeAsync(SonosDevice? coordinator, SonosDevice? room)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await UnsubscribeAllCoreAsync().ConfigureAwait(false);

            if (coordinator != null)
            {
                await SubscribeAsync("AVTransport", coordinator.Host, AvTransportEventPath, coordinator.Ip)
                    .ConfigureAwait(false);
                await SubscribeAsync("Queue", coordinator.Host, QueueEventPath, coordinator.Ip)
                    .ConfigureAwait(false);
            }

            if (room != null)
            {
                await SubscribeAsync("RenderingControl", room.Host, RenderingEventPath, room.Ip)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SubscribeAsync(string serviceName, string host, string eventPath, IPAddress speakerIp)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var callback = _server.BuildCallbackUrl(token, speakerIp);

        try
        {
            using var request = new HttpRequestMessage(
                new HttpMethod("SUBSCRIBE"), $"http://{host}:{SonosSoap.SonosPort}{eventPath}");
            request.Headers.TryAddWithoutValidation("CALLBACK", $"<{callback}>");
            request.Headers.TryAddWithoutValidation("NT", "upnp:event");
            request.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{RequestedTimeoutSeconds}");

            using var response = await SonosSoap.Client.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log?.Invoke($"SUBSCRIBE {serviceName} failed: HTTP {(int)response.StatusCode}.");
                return;
            }

            var sid = FirstHeader(response, "SID");
            if (string.IsNullOrEmpty(sid))
            {
                Log?.Invoke($"SUBSCRIBE {serviceName} returned no SID.");
                return;
            }

            _byToken[token] = new Subscription
            {
                Token = token,
                ServiceName = serviceName,
                Host = host,
                EventPath = eventPath,
                Sid = sid,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(ParseTimeout(FirstHeader(response, "TIMEOUT")))
            };

            Log?.Invoke($"Subscribed to {serviceName} events on {host}.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"SUBSCRIBE {serviceName} failed: {ex.Message}");
        }
    }

    private async Task RenewDueAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var due = _byToken.Values
                .Where(s => s.ExpiresUtc - DateTime.UtcNow < TimeSpan.FromMinutes(5))
                .ToList();

            foreach (var subscription in due)
                await RenewAsync(subscription).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Renewing event subscriptions failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RenewAsync(Subscription subscription)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"),
                $"http://{subscription.Host}:{SonosSoap.SonosPort}{subscription.EventPath}");
            request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
            request.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{RequestedTimeoutSeconds}");

            using var response = await SonosSoap.Client.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                subscription.ExpiresUtc =
                    DateTime.UtcNow.AddSeconds(ParseTimeout(FirstHeader(response, "TIMEOUT")));
                return;
            }

            // The player forgot us (it rebooted, or the subscription lapsed). Start over.
            _byToken.TryRemove(subscription.Token, out _);
            await SubscribeAsync(subscription.ServiceName, subscription.Host,
                subscription.EventPath, IPAddress.Parse(subscription.Host)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Renewing {subscription.ServiceName} failed: {ex.Message}");
        }
    }

    public async Task UnsubscribeAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await UnsubscribeAllCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task UnsubscribeAllCoreAsync()
    {
        foreach (var subscription in _byToken.Values.ToList())
        {
            _byToken.TryRemove(subscription.Token, out _);
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"),
                    $"http://{subscription.Host}:{SonosSoap.SonosPort}{subscription.EventPath}");
                request.Headers.TryAddWithoutValidation("SID", subscription.Sid);
                using var response = await SonosSoap.Client.SendAsync(request).ConfigureAwait(false);
            }
            catch
            {
                // The player may already be gone; nothing useful to do.
            }
        }
    }

    private void OnNotification(string token, string body)
    {
        if (!_byToken.TryGetValue(token, out var subscription)) return;
        Notified?.Invoke(subscription.ServiceName, body);
    }

    private static string FirstHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? "" : "";

    private static int ParseTimeout(string headerValue)
    {
        // "Second-1800", or "Second-infinite".
        if (!string.IsNullOrEmpty(headerValue))
        {
            var dash = headerValue.IndexOf('-');
            if (dash >= 0 && int.TryParse(headerValue[(dash + 1)..], out var seconds) && seconds > 0)
                return seconds;
        }
        return RequestedTimeoutSeconds;
    }

    public void Dispose()
    {
        _renewTimer.Dispose();
        _server.GenaNotification -= OnNotification;

        // Best effort: give the players a moment to hear the UNSUBSCRIBE on the way out.
        try { UnsubscribeAllAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }

        _gate.Dispose();
    }
}
