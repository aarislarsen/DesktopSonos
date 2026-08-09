using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DesktopSonos.Audio;

namespace DesktopSonos.Serving;

/// <summary>
/// A small HTTP/1.1 server that exists purely so Sonos players have something to pull from.
///
/// Built on TcpListener rather than HttpListener on purpose: HttpListener needs an
/// administrator-created URL ACL for any non-localhost prefix, and we also need precise
/// control over range requests and never-ending responses.
///
/// Routes:
///   GET/HEAD /media/{token}{ext}  - a registered local or UNC file, with byte-range support
///   GET/HEAD /stream.mp3          - the live desktop-audio feed (no Content-Length, never ends)
///   GET      /health              - liveness probe for the UI
/// </summary>
public sealed class HttpMediaServer : IDisposable
{
    private const int HeaderLimitBytes = 16 * 1024;
    private const int FileChunkSize = 64 * 1024;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;
    public MediaRegistry Registry { get; } = new();
    public LoopbackStreamer? Streamer { get; set; }

    public event Action<string>? Log;

    /// <summary>Raised with (callback token, NOTIFY body) when a player reports an event.</summary>
    public event Action<string, string>? GenaNotification;

    public void Start(int preferredPort = 8099)
    {
        if (_listener != null) return;

        for (var port = preferredPort; port < preferredPort + 40; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (SocketException)
            {
                // in use — try the next one
            }
        }

        if (_listener == null)
            throw new IOException($"No free TCP port in {preferredPort}-{preferredPort + 39}.");

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log?.Invoke($"Media server listening on port {Port}.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    /// <summary>URL for a registered file, using the local IP that can reach <paramref name="speakerIp"/>.</summary>
    public string BuildFileUrl(string fullPath, IPAddress speakerIp)
    {
        var token = Registry.Register(fullPath);
        var ext = Path.GetExtension(fullPath);
        var local = NetworkUtil.GetLocalAddressFor(speakerIp);
        return $"http://{local}:{Port}/media/{token}{ext}";
    }

    /// <summary>
    /// The desktop-audio URL. The x-rincon-mp3radio scheme tells Sonos to treat it as a
    /// live stream (no seeking, no duration, auto-reconnect) instead of a file.
    /// </summary>
    public string BuildStreamUrl(IPAddress speakerIp)
    {
        var local = NetworkUtil.GetLocalAddressFor(speakerIp);
        return $"x-rincon-mp3radio://{local}:{Port}/stream.mp3";
    }

    /// <summary>
    /// Takes a media URL a player is already holding — possibly from a previous session, with a
    /// stale host or port — and returns the equivalent URL for this session, or null if it is
    /// not one of ours or its token is unknown.
    /// </summary>
    public string? TryRebuildMediaUrl(string uri, IPAddress speakerIp)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttp) return null;
        if (!parsed.AbsolutePath.StartsWith("/media/", StringComparison.OrdinalIgnoreCase)) return null;

        var segment = parsed.AbsolutePath["/media/".Length..];
        var dot = segment.IndexOf('.');
        var token = dot > 0 ? segment[..dot] : segment;

        return Registry.TryResolve(token, out var fullPath)
            ? BuildFileUrl(fullPath, speakerIp)
            : null;
    }

    /// <summary>Where a player should POST its GENA NOTIFY messages for one subscription.</summary>
    public string BuildCallbackUrl(string token, IPAddress speakerIp)
    {
        var local = NetworkUtil.GetLocalAddressFor(speakerIp);
        return $"http://{local}:{Port}/gena/{token}";
    }

    // ---------------------------------------------------------------- plumbing

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener == null) return;

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                client.SendTimeout = 30_000;
                client.ReceiveTimeout = 30_000;

                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request == null) return;

                var path = request.Path;
                if (path.StartsWith("/gena/", StringComparison.OrdinalIgnoreCase))
                    await ServeGenaCallbackAsync(stream, request, ct).ConfigureAwait(false);
                else if (path.Equals("/stream.mp3", StringComparison.OrdinalIgnoreCase))
                    await ServeLiveStreamAsync(stream, request, ct).ConfigureAwait(false);
                else if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
                    await ServeFileAsync(stream, request, ct).ConfigureAwait(false);
                else if (path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                    await WriteTextAsync(stream, 200, "OK", "ok", ct).ConfigureAwait(false);
                else
                    await WriteTextAsync(stream, 404, "Not Found", "not found", ct).ConfigureAwait(false);
            }
        }
        catch (IOException) { /* client vanished */ }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"Request failed: {ex.Message}");
        }
    }

    private sealed class HttpRequest
    {
        public string Method { get; init; } = "GET";
        public string Path { get; init; } = "/";
        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string Body { get; set; } = "";
        public bool IsHead => Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GENA NOTIFY from a player. Answering 200 promptly keeps the subscription healthy.</summary>
    private async Task ServeGenaCallbackAsync(NetworkStream stream, HttpRequest request, CancellationToken ct)
    {
        var token = request.Path["/gena/".Length..].Trim('/');

        const string ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(ok), ct).ConfigureAwait(false);

        if (request.Body.Length > 0 && token.Length > 0)
        {
            try { GenaNotification?.Invoke(token, request.Body); }
            catch (Exception ex) { Log?.Invoke($"Event handler threw: {ex.Message}"); }
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(1024);
        var single = new byte[1];
        var matched = 0; // how much of CR LF CR LF we have seen

        while (matched < 4)
        {
            int read;
            try { read = await stream.ReadAsync(single.AsMemory(0, 1), ct).ConfigureAwait(false); }
            catch { return null; }
            if (read == 0) return null;

            var b = single[0];
            buffer.Add(b);
            matched = (matched, b) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0
            };

            if (buffer.Count > HeaderLimitBytes) return null;
        }

        var text = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrEmpty(lines[i])) break;
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        var target = requestLine[1];
        var query = target.IndexOf('?');
        if (query >= 0) target = target[..query];

        var result = new HttpRequest
        {
            Method = requestLine[0],
            Path = Uri.UnescapeDataString(target),
            Headers = headers
        };

        // GENA NOTIFY carries an XML body; media requests never do.
        if (headers.TryGetValue("Content-Length", out var lengthText) &&
            int.TryParse(lengthText, out var contentLength) &&
            contentLength > 0 && contentLength <= 512 * 1024)
        {
            var body = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                int read;
                try { read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false); }
                catch { break; }
                if (read <= 0) break;
                offset += read;
            }
            result.Body = Encoding.UTF8.GetString(body, 0, offset);
        }

        return result;
    }

    // ---------------------------------------------------------------- file route

    private async Task ServeFileAsync(NetworkStream stream, HttpRequest request, CancellationToken ct)
    {
        // /media/{token}{ext}
        var segment = request.Path["/media/".Length..];
        var dot = segment.IndexOf('.');
        var token = dot > 0 ? segment[..dot] : segment;

        if (!Registry.TryResolve(token, out var fullPath) || !File.Exists(fullPath))
        {
            await WriteTextAsync(stream, 404, "Not Found", "unknown media id", ct).ConfigureAwait(false);
            return;
        }

        FileStream file;
        try
        {
            file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                FileChunkSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Cannot open {fullPath}: {ex.Message}");
            await WriteTextAsync(stream, 503, "Service Unavailable", "cannot open file", ct).ConfigureAwait(false);
            return;
        }

        await using (file)
        {
            var length = file.Length;
            long start = 0, end = length - 1;
            var partial = false;

            if (request.Headers.TryGetValue("Range", out var range) &&
                range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var spec = range[6..].Split(',')[0].Trim();
                var dash = spec.IndexOf('-');
                if (dash >= 0)
                {
                    var fromText = spec[..dash];
                    var toText = spec[(dash + 1)..];

                    if (fromText.Length > 0 && long.TryParse(fromText, out var from))
                    {
                        start = from;
                        if (toText.Length > 0 && long.TryParse(toText, out var to)) end = to;
                    }
                    else if (toText.Length > 0 && long.TryParse(toText, out var suffix))
                    {
                        start = Math.Max(0, length - suffix);
                    }

                    if (start >= length || start < 0)
                    {
                        var invalid = new StringBuilder()
                            .Append("HTTP/1.1 416 Range Not Satisfiable\r\n")
                            .Append($"Content-Range: bytes */{length}\r\n")
                            .Append("Content-Length: 0\r\n")
                            .Append("Connection: close\r\n\r\n")
                            .ToString();
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(invalid), ct).ConfigureAwait(false);
                        return;
                    }

                    end = Math.Min(end, length - 1);
                    partial = true;
                }
            }

            var count = end - start + 1;
            var headers = new StringBuilder();
            headers.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
            headers.Append($"Content-Type: {MimeTypes.ForFile(fullPath)}\r\n");
            headers.Append($"Content-Length: {count}\r\n");
            headers.Append("Accept-Ranges: bytes\r\n");
            if (partial) headers.Append($"Content-Range: bytes {start}-{end}/{length}\r\n");
            headers.Append("Connection: close\r\n\r\n");

            await stream.WriteAsync(Encoding.ASCII.GetBytes(headers.ToString()), ct).ConfigureAwait(false);
            if (request.IsHead) return;

            file.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[FileChunkSize];
            var remaining = count;

            while (remaining > 0 && !ct.IsCancellationRequested)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await file.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
        }
    }

    // ---------------------------------------------------------------- live stream route

    private async Task ServeLiveStreamAsync(NetworkStream stream, HttpRequest request, CancellationToken ct)
    {
        var streamer = Streamer;
        if (streamer is null || !streamer.IsRunning)
        {
            await WriteTextAsync(stream, 503, "Service Unavailable", "desktop streaming is off", ct)
                .ConfigureAwait(false);
            return;
        }

        // Kept to the minimum a decoder needs. icy-* headers make some clients switch to
        // Shoutcast framing and expect an "ICY 200 OK" status line instead of HTTP.
        var headers = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: audio/mpeg\r\n")
            .Append("Cache-Control: no-cache\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).ConfigureAwait(false);
        if (request.IsHead) return;

        long sent = 0;
        var lastReport = 0L;

        using var subscription = streamer.Broadcaster.Subscribe();

        Log?.Invoke($"Listener attached — {streamer.Broadcaster.BufferedBytes / 1024} KB backlog, " +
                    $"burst frame-aligned at offset {streamer.Broadcaster.LastBurstOffset}.");
        try
        {
            await foreach (var chunk in subscription.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                sent += chunk.Length;

                // Distinguishes "nothing is being sent" from "sent but not decodable".
                if (sent - lastReport >= 256 * 1024)
                {
                    lastReport = sent;
                    Log?.Invoke($"Streamed {sent / 1024} KB to the listener.");
                }
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"Stream write failed after {sent / 1024} KB: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Log?.Invoke($"Listener detached after {sent / 1024} KB.");
        }
    }

    private static async Task WriteTextAsync(NetworkStream stream, int status, string reason,
        string body, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append($"Content-Length: {payload.Length}\r\n")
            .Append("Connection: close\r\n\r\n")
            .ToString();

        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }
}
