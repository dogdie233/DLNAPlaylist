using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace DLNAPlaylist.Dlna.Soap;

/// <summary>
///     轻量 SOAP 客户端：POST + SOAPACTION 头，解析响应或故障。
/// </summary>
public sealed class SoapClient : IDisposable
{
    private readonly HttpClient _http;

    public SoapClient()
    {
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(DlnaConstants.SsdpUserAgent);
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    public async Task<XElement> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string actionName,
        IReadOnlyList<(string name, string value)> args,
        CancellationToken ct)
    {
        var body = BuildEnvelope(serviceType, actionName, args);
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{actionName}\"");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            // 尝试解析 SOAP Fault
            throw new SoapFaultException(
                $"SOAP {actionName} failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n{text}");

        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new SoapFaultException($"SOAP {actionName}: malformed response: {ex.Message}\n{text}");
        }

        // 返回 <actionName + "Response"> 节点
        var responseName = actionName + "Response";
        var respEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == responseName);
        if (respEl is null)
            throw new SoapFaultException($"SOAP {actionName}: missing {responseName}\n{text}");
        return respEl;
    }

    private static string BuildEnvelope(string serviceType, string actionName,
        IReadOnlyList<(string name, string value)> args)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.Append(
            """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">""");
        sb.Append("<s:Body>");
        sb.Append("<u:").Append(actionName).Append(" xmlns:u=\"").Append(serviceType).Append("\">");
        foreach (var (n, v) in args)
        {
            sb.Append('<').Append(n).Append('>');
            sb.Append(WebUtility.HtmlEncode(v));
            sb.Append("</").Append(n).Append('>');
        }

        sb.Append("</u:").Append(actionName).Append('>');
        sb.Append("</s:Body></s:Envelope>");
        return sb.ToString();
    }
}

public sealed class SoapFaultException : Exception
{
    public SoapFaultException(string message) : base(message)
    {
    }
}