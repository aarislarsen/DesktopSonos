using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace DesktopSonos.Sonos;

/// <summary>
/// Minimal UPnP/SOAP client. Sonos players expose their control endpoints on port 1400.
/// </summary>
public static class SonosSoap
{
    public const int SonosPort = 1400;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        // Deadlines are per call, below — this is only a backstop.
        Timeout = TimeSpan.FromSeconds(90)
    };

    /// <summary>Fine for the quick query/command actions that make up most traffic.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Loading a live stream is different: Sonos connects to the URL and waits for audio before
    /// it answers, so this call legitimately takes far longer than a normal command.
    /// </summary>
    public static readonly TimeSpan StreamLoadTimeout = TimeSpan.FromSeconds(45);

    public static HttpClient Client => Http;

    /// <summary>Invokes a SOAP action and returns the response arguments keyed by element name.</summary>
    public static async Task<Dictionary<string, string>> InvokeAsync(
        string host,
        string controlPath,
        string serviceType,
        string action,
        IEnumerable<KeyValuePair<string, string>>? args = null,
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var deadline = timeout ?? DefaultTimeout;
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        body.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        body.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        body.Append($"<u:{action} xmlns:u=\"{serviceType}\">");
        if (args != null)
        {
            foreach (var kv in args)
                body.Append($"<{kv.Key}>{Xml.Escape(kv.Value)}</{kv.Key}>");
        }
        body.Append($"</u:{action}></s:Body></s:Envelope>");

        var url = $"http://{host}:{SonosPort}{controlPath}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);

        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                             .ConfigureAwait(false);
            text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SonosException(
                $"{action} on {host} got no answer within {deadline.TotalSeconds:0} seconds. " +
                "For a stream this usually means the player could not pull audio back from this " +
                "PC — check that Windows Firewall allows DesktopSonos on the private network.");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var (code, detail) = ParseFault(text);
                throw new SonosException($"{action} failed on {host}: {detail}", code);
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(text);
            var response = doc.Descendants()
                              .FirstOrDefault(e => e.Name.LocalName == action + "Response");
            if (response != null)
            {
                foreach (var child in response.Elements())
                    result[child.Name.LocalName] = child.Value;
            }
        }
        catch (System.Xml.XmlException)
        {
            // Some actions legitimately return an empty body.
        }
        return result;
    }

    /// <summary>Pulls the UPnP error code out of a SOAP fault body.</summary>
    private static (int Code, string Detail) ParseFault(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var codeText = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            if (codeText != null && int.TryParse(codeText, out var code))
                return (code, $"UPnP error {code} — {UpnpErrorText(code)}");

            var faultString = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
            return (0, faultString ?? "unrecognised SOAP fault");
        }
        catch
        {
            return (0, "unrecognised SOAP fault");
        }
    }

    public static string UpnpErrorText(int code) => code switch
    {
        402 => "invalid arguments",
        701 => "transition not available: nothing is loaded on the player, or it is still " +
               "switching sources, or the command went to a grouped follower instead of its coordinator",
        711 => "illegal seek target",
        714 => "unsupported media / MIME type",
        716 => "the player could not fetch the URL (check Windows Firewall and that the PC and " +
               "speaker are on the same subnet)",
        718 => "invalid instance, or the queue is empty",
        800 => "command not supported on a grouped member; send it to the coordinator",
        _ => "see the UPnP AV error codes"
    };
}

public sealed class SonosException : Exception
{
    public SonosException(string message, int errorCode = 0) : base(message) => ErrorCode = errorCode;

    /// <summary>UPnP error code, or 0 if the fault could not be parsed.</summary>
    public int ErrorCode { get; }
}

public static class Xml
{
    /// <summary>XML-escapes a value for embedding in a SOAP argument.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value!.Length + 16);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    // Strip control characters that would make the envelope invalid.
                    if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r') sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
