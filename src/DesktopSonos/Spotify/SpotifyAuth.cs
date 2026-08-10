using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesktopSonos.Persistence;

namespace DesktopSonos.Spotify;

/// <summary>
/// Signs in to Spotify with the Authorization Code + PKCE flow: the default browser does the
/// login (so an existing Spotify session makes it one click) and hands the code back to a
/// listener bound to the loopback address. PKCE rather than the client-secret flow because a
/// desktop app cannot keep a secret — there is nothing here worth extracting from the binary.
/// </summary>
public sealed class SpotifyAuth
{
    /// <summary>
    /// Read-only throughout. Playlists made in this app are Sonos saved queues, which live on the
    /// players, so nothing ever needs write access to the Spotify account.
    /// </summary>
    public const string Scopes =
        "user-read-private user-read-email user-library-read " +
        "playlist-read-private playlist-read-collaborative";

    private const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";

    /// <summary>
    /// Tokens last an hour. Refreshing a minute early avoids a request failing mid-flight on the
    /// boundary, which would otherwise show up as a random "search failed".
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private SpotifyStore _store = SpotifyStore.Load();
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry = DateTimeOffset.MinValue;

    public event Action<string>? Log;

    /// <summary>From the user's own Spotify dashboard. There is no shared id to fall back on.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>
    /// Fixed, because Spotify only accepts redirect URIs registered on the application, so it has
    /// to be the same port every run. Configurable only for the case where something else on the
    /// machine already owns it.
    /// </summary>
    public int RedirectPort { get; set; } = 8098;

    public string RedirectUri => $"http://127.0.0.1:{RedirectPort}/callback";

    public bool IsConnected => _store.ReadRefreshToken() is not null;
    public string DisplayName => _store.DisplayName;
    public string UserId => _store.UserId;

    /// <summary>
    /// Runs the browser sign-in. Returns the display name on success. The listener is opened
    /// *before* the browser, so a very fast redirect cannot arrive at a closed port.
    /// </summary>
    public async Task<string> ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
            throw new InvalidOperationException(
                "No Spotify client id yet — create an app at developer.spotify.com and paste its " +
                "Client ID into the Spotify settings.");

        var verifier = RandomUrlSafe(64);
        var challenge = Challenge(verifier);
        var state = RandomUrlSafe(16);

        using var listener = new LoopbackCallback(RedirectPort);
        listener.Start();

        var url =
            $"{AuthorizeEndpoint}?client_id={Uri.EscapeDataString(ClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&state={state}" +
            $"&scope={Uri.EscapeDataString(Scopes)}" +
            "&code_challenge_method=S256" +
            $"&code_challenge={challenge}";

        Log?.Invoke("Opening the browser to sign in to Spotify…");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

        var (code, returnedState, error) = await listener.WaitAsync(TimeSpan.FromMinutes(5), ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException($"Spotify refused the sign-in: {error}");
        if (returnedState != state)
            throw new InvalidOperationException("The sign-in response did not match the request.");
        if (string.IsNullOrEmpty(code))
            throw new InvalidOperationException("No authorisation code came back from Spotify.");

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier
        });

        using var response = await Http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Spotify rejected the token request: {Describe(body)}");

        ApplyTokenResponse(body);

        var refresh = _store.ReadRefreshToken();
        if (refresh is null)
            throw new InvalidOperationException("Spotify did not return a refresh token.");

        _store.Save();
        return _store.DisplayName;
    }

    /// <summary>Records who signed in, so the settings drawer can name the account.</summary>
    public void RememberAccount(string displayName, string userId)
    {
        _store.DisplayName = displayName;
        _store.UserId = userId;
        _store.Save();
    }

    public void Disconnect()
    {
        _accessToken = null;
        _accessTokenExpiry = DateTimeOffset.MinValue;
        _store = new SpotifyStore();
        SpotifyStore.Delete();
    }

    /// <summary>
    /// The access token every API call needs. Cached until it is nearly expired, then renewed
    /// from the refresh token without any browser involvement. Returns null when nobody is
    /// signed in, which callers treat as "show the connect button", not as an error.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow + RefreshMargin < _accessTokenExpiry)
            return _accessToken;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // A second caller can have refreshed it while this one waited for the gate.
            if (_accessToken is not null && DateTimeOffset.UtcNow + RefreshMargin < _accessTokenExpiry)
                return _accessToken;

            var refresh = _store.ReadRefreshToken();
            if (refresh is null) return null;

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = ClientId
            });

            using var response = await Http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // A revoked or invalidated refresh token can never come good on a retry, so the
                // link is dropped rather than left failing every call until the app restarts.
                Log?.Invoke($"Spotify sign-in expired: {Describe(body)}");
                Disconnect();
                return null;
            }

            ApplyTokenResponse(body);
            _store.Save();
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }

    /// <summary>
    /// Spotify only returns a new refresh token some of the time; when it does not, the existing
    /// one stays valid and must be kept rather than overwritten with an empty string.
    /// </summary>
    private void ApplyTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        _accessToken = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;

        var seconds = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
        _accessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(seconds);

        if (root.TryGetProperty("refresh_token", out var refresh) &&
            refresh.GetString() is { Length: > 0 } token)
        {
            _store.WriteRefreshToken(token);
        }
    }

    private static string Describe(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_description", out var description))
                return description.GetString() ?? body;
            if (root.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? body
                    : error.ToString();
        }
        catch { /* not JSON; the raw body is the best description available */ }

        return body.Length > 200 ? body[..200] : body;
    }

    // ---------------------------------------------------------------- PKCE

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// A one-request HTTP server on the loopback address, just long enough to catch Spotify's
/// redirect. TcpListener rather than HttpListener so there is never a URL-ACL prompt, which is
/// the same reason the media server is built this way.
/// </summary>
internal sealed class LoopbackCallback : IDisposable
{
    private const string Page =
        "<!doctype html><meta charset=\"utf-8\"><title>DesktopSonos</title>" +
        "<body style=\"background:#0e1013;color:#e9ecf0;font:15px/1.6 system-ui;text-align:center;padding:80px\">" +
        "<h1 style=\"font-weight:600\">Spotify connected</h1>" +
        "<p style=\"color:#868e9a\">You can close this tab and go back to DesktopSonos.</p>";

    private readonly TcpListener _listener;

    public LoopbackCallback(int port) =>
        _listener = new TcpListener(IPAddress.Loopback, port);

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Port {((IPEndPoint)_listener.LocalEndpoint).Port} is already in use, so the " +
                "Spotify sign-in cannot be received. Change the redirect port in the Spotify " +
                $"settings and on the Spotify dashboard. ({ex.SocketErrorCode})", ex);
        }
    }

    /// <summary>
    /// Waits for the browser to arrive with the code. Browsers commonly probe with a favicon
    /// request first, so anything that is not the callback path is answered and ignored.
    /// </summary>
    public async Task<(string? Code, string? State, string? Error)> WaitAsync(TimeSpan timeout,
        CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read <= 0) continue;

            var request = Encoding.ASCII.GetString(buffer, 0, read);
            var line = request.Split('\r', '\n')[0];
            var parts = line.Split(' ');
            var target = parts.Length > 1 ? parts[1] : "/";

            var query = target.IndexOf('?') is var mark && mark >= 0
                ? HttpUtilityLite.ParseQuery(target[(mark + 1)..])
                : new Dictionary<string, string>();

            var isCallback = query.ContainsKey("code") || query.ContainsKey("error");

            var body = Encoding.UTF8.GetBytes(isCallback ? Page : "");
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(isCallback ? "200 OK" : "404 Not Found")}\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");

            await stream.WriteAsync(head, deadline.Token).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body, deadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(deadline.Token).ConfigureAwait(false);

            if (!isCallback) continue;

            return (query.GetValueOrDefault("code"), query.GetValueOrDefault("state"),
                query.GetValueOrDefault("error"));
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* already down */ }
    }
}

/// <summary>
/// Query parsing without pulling in System.Web. Only ever sees Spotify's own redirect.
/// </summary>
internal static class HttpUtilityLite
{
    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0) continue;
            result[Uri.UnescapeDataString(pair[..equals])] =
                Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return result;
    }
}
