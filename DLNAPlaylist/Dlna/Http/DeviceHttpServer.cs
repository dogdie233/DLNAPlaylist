using System.Net;
using System.Text;
using System.Xml.Linq;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Http;

/// <summary>
/// 基于 HttpListener 的设备 HTTP 服务器。
/// 承载：设备描述、三服务 SCPD、三服务 SOAP 控制端点、GENA 事件订阅（最小实现）。
///
/// SOAP 分发通过注册 handler 完成：
///   Register("AVTransport", "SetAVTransportURI", async (args, reqCtx) => ...)
/// </summary>
public sealed class DeviceHttpServer : IAsyncDisposable
{
    readonly HttpListener _listener = new();
    readonly LogSink _log;
    readonly string _udn;
    readonly IPAddress _bindAddr;
    readonly int _port;
    readonly string _friendlyName;
    readonly CancellationTokenSource _cts = new();
    Task? _loopTask;

    // key: (serviceShortName, actionName)
    readonly Dictionary<(string, string), Func<SoapRequest, Task<IReadOnlyList<(string, string)>>>> _handlers = new();

    public DeviceHttpServer(IPAddress bindAddr, int port, string udn, string friendlyName, LogSink log)
    {
        _bindAddr = bindAddr;
        _port = port;
        _udn = udn;
        _friendlyName = friendlyName;
        _log = log;
        // HttpListener 在 Windows 上用 + 通配会需要 urlacl；这里直接绑定到具体 IP 避免权限问题
        _listener.Prefixes.Add($"http://{bindAddr}:{port}/");
    }

    public int Port => _port;

    public void Register(string serviceShortName, string actionName,
        Func<SoapRequest, Task<IReadOnlyList<(string, string)>>> handler)
    {
        _handlers[(serviceShortName, actionName)] = handler;
    }

    public void Start()
    {
        _listener.Start();
        _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info("HTTP", $"Device HTTP server on http://{_bindAddr}:{_port}/");
    }

    async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(ctx, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _log.Error("HTTP", $"handler error: {ex.Message}");
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
                }
            }, ct);
        }
    }

    async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var path = req.Url?.AbsolutePath ?? "/";
        var method = req.HttpMethod;

        // GENA SUBSCRIBE/UNSUBSCRIBE：最小实现 → 直接 200
        if (method is "SUBSCRIBE" or "UNSUBSCRIBE")
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["SID"] = "uuid:" + Guid.NewGuid();
            ctx.Response.Headers["TIMEOUT"] = "Second-1800";
            ctx.Response.Close();
            return;
        }

        if (method == "GET")
        {
            switch (path.ToLowerInvariant())
            {
                case "/device.xml":
                    await WriteXmlAsync(ctx, DeviceXml.Device(_udn, _friendlyName, _port, _bindAddr.ToString()));
                    return;
                case "/scpd/avtransport.xml":
                    await WriteXmlAsync(ctx, DeviceXml.AvTransportScpd);
                    return;
                case "/scpd/connectionmanager.xml":
                    await WriteXmlAsync(ctx, DeviceXml.ConnectionManagerScpd);
                    return;
                case "/scpd/renderingcontrol.xml":
                    await WriteXmlAsync(ctx, DeviceXml.RenderingControlScpd);
                    return;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
            }
        }

        if (method == "POST" && path.StartsWith("/ctrl/", StringComparison.OrdinalIgnoreCase))
        {
            var service = path.Substring("/ctrl/".Length).TrimEnd('/').ToLowerInvariant();
            var shortName = service switch
            {
                "avtransport" => "AVTransport",
                "connectionmanager" => "ConnectionManager",
                "renderingcontrol" => "RenderingControl",
                _ => null,
            };
            if (shortName is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            await HandleSoapAsync(ctx, shortName, ct).ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
    }

    async Task HandleSoapAsync(HttpListenerContext ctx, string serviceShortName, CancellationToken ct)
    {
        string body;
        using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = await sr.ReadToEndAsync(ct).ConfigureAwait(false);

        string? actionName = null;
        string? serviceType = null;
        var args = new List<(string, string)>();

        try
        {
            var doc = XDocument.Parse(body);
            var actionEl = doc.Descendants()
                .FirstOrDefault(e => e.Parent is not null && e.Parent.Name.LocalName == "Body");
            if (actionEl is null)
            {
                await WriteSoapFaultAsync(ctx, "Client", "Action not found");
                return;
            }
            actionName = actionEl.Name.LocalName;
            serviceType = actionEl.Name.NamespaceName;
            foreach (var arg in actionEl.Elements())
                args.Add((arg.Name.LocalName, arg.Value));
        }
        catch (Exception ex)
        {
            await WriteSoapFaultAsync(ctx, "Client", $"Bad SOAP: {ex.Message}");
            return;
        }

        if (!_handlers.TryGetValue((serviceShortName, actionName!), out var handler))
        {
            _log.Warn("SOAP", $"Unhandled {serviceShortName}#{actionName}");
            await WriteSoapFaultAsync(ctx, "Client", "Action not implemented", 501, 401);
            return;
        }

        var soapReq = new SoapRequest(
            ServiceShortName: serviceShortName,
            ServiceType: serviceType!,
            ActionName: actionName!,
            Args: args,
            RemoteIp: ctx.Request.RemoteEndPoint?.Address.ToString() ?? "",
            UserAgent: ctx.Request.Headers["USER-AGENT"] ?? ctx.Request.UserAgent ?? "",
            XAvClientInfo: ctx.Request.Headers["X-AV-Client-Info"] ?? "");

        IReadOnlyList<(string, string)> outArgs;
        try
        {
            outArgs = await handler(soapReq).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("SOAP", $"{serviceShortName}#{actionName} threw: {ex.Message}");
            await WriteSoapFaultAsync(ctx, "Server", ex.Message);
            return;
        }

        await WriteSoapResponseAsync(ctx, serviceType!, actionName!, outArgs).ConfigureAwait(false);
    }

    static async Task WriteXmlAsync(HttpListenerContext ctx, string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    static async Task WriteSoapResponseAsync(HttpListenerContext ctx, string serviceType, string actionName, IReadOnlyList<(string, string)> outArgs)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.Append("""<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">""");
        sb.Append("<s:Body>");
        sb.Append("<u:").Append(actionName).Append("Response xmlns:u=\"").Append(serviceType).Append("\">");
        foreach (var (n, v) in outArgs)
        {
            sb.Append('<').Append(n).Append('>');
            sb.Append(WebUtility.HtmlEncode(v));
            sb.Append("</").Append(n).Append('>');
        }
        sb.Append("</u:").Append(actionName).Append("Response>");
        sb.Append("</s:Body></s:Envelope>");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    static async Task WriteSoapFaultAsync(HttpListenerContext ctx, string faultCode, string faultString, int httpStatus = 500, int upnpError = 501)
    {
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><s:Fault><faultcode>s:{faultCode}</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>{upnpError}</errorCode><errorDescription>{WebUtility.HtmlEncode(faultString)}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>""";
        var bytes = Encoding.UTF8.GetBytes(xml);
        ctx.Response.StatusCode = httpStatus;
        ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { if (_loopTask is not null) await _loopTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}

public sealed record SoapRequest(
    string ServiceShortName,
    string ServiceType,
    string ActionName,
    IReadOnlyList<(string Name, string Value)> Args,
    string RemoteIp,
    string UserAgent,
    string XAvClientInfo)
{
    public string? Get(string name) =>
        Args.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
}
