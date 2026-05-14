using System.Net;
using System.Text;
using System.Xml.Linq;

using DLNAPlaylist.Core;
using DLNAPlaylist.Media;
using DLNAPlaylist.Util;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace DLNAPlaylist.Dlna.Http;

/// <summary>
///     设备 HTTP endpoints（Kestrel + Minimal API）。
///     不再自己起 WebApplication —— 由 Program.cs 的主 host 起 Kestrel，
///     这里只负责往 IEndpointRouteBuilder 上挂端点。
///
///     选 Kestrel 而不是 HttpListener：HttpListener 监听具体 IP 时要 URL ACL，
///     普通用户得管理员权限或事先 netsh add urlacl。Kestrel 是纯 socket，无此限制。
///
///     承载：设备描述、三服务 SCPD、三服务 SOAP 控制端点、GENA 事件订阅（最小实现）。
///     注意：DLNA 用的 SUBSCRIBE/UNSUBSCRIBE 不是标准 HTTP 方法，用 MapMethods 接受任意 verb。
/// </summary>
public sealed class DeviceHttpEndpoints(AppOptions opts, LogSink log, MediaCache cache)
{
    private readonly Dictionary<(string, string), Func<SoapRequest, Task<IReadOnlyList<(string, string)>>>> _handlers = new();

    public void Register(string serviceShortName, string actionName,
        Func<SoapRequest, Task<IReadOnlyList<(string, string)>>> handler)
    {
        _handlers[(serviceShortName, actionName)] = handler;
    }

    /// <summary>把所有 DLNA 端点挂到指定的 IEndpointRouteBuilder。</summary>
    public void MapTo(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/device.xml", () =>
            Results.Content(DeviceXml.Device(opts.Udn, opts.FriendlyName, opts.HttpPort, opts.BindAddress.ToString()),
                "text/xml; charset=\"utf-8\""));
        endpoints.MapGet("/scpd/avtransport.xml", () =>
            Results.Content(DeviceXml.AvTransportScpd, "text/xml; charset=\"utf-8\""));
        endpoints.MapGet("/scpd/connectionmanager.xml", () =>
            Results.Content(DeviceXml.ConnectionManagerScpd, "text/xml; charset=\"utf-8\""));
        endpoints.MapGet("/scpd/renderingcontrol.xml", () =>
            Results.Content(DeviceXml.RenderingControlScpd, "text/xml; charset=\"utf-8\""));

        endpoints.MapPost("/ctrl/{service}", HandleSoapAsync);

        endpoints.MapMethods("/event/{service}", ["SUBSCRIBE", "UNSUBSCRIBE"], (HttpResponse resp) =>
        {
            resp.Headers["SID"] = "uuid:" + Guid.NewGuid();
            resp.Headers["TIMEOUT"] = "Second-1800";
            return Results.Ok();
        });

        // 本地缓存流：用于"投屏端临时流"被改写后的回放
        // {name} 仅作为文件名提示（部分电视会按扩展名挑解码器），实际只看 {id}
        endpoints.MapMethods("/cache/{id}/{name}", ["GET", "HEAD"], HandleCacheStreamAsync);

        log.Info("HTTP", $"Endpoints mapped (will listen on http://{opts.BindAddress}:{opts.HttpPort}/)");
    }

    private async Task<IResult> HandleCacheStreamAsync(string id, string name, HttpRequest req, HttpResponse resp)
    {
        if (!Guid.TryParseExact(id, "N", out var guid))
        {
            return Results.NotFound();
        }
        if (!cache.TryGet(guid, out var entry))
        {
            return Results.NotFound();
        }

        resp.Headers[HeaderNames.AcceptRanges] = "bytes";
        resp.Headers["Content-Disposition"] = $"inline; filename=\"{entry.FileName}\"";

        var length = entry.Length;
        var rangeHeader = req.Headers[HeaderNames.Range].ToString();

        long start = 0;
        long endInclusive = length - 1;
        var partial = false;

        if (!string.IsNullOrEmpty(rangeHeader)
            && RangeHeaderValue.TryParse(rangeHeader, out var parsed)
            && parsed.Unit == "bytes"
            && parsed.Ranges.Count == 1)
        {
            var r = parsed.Ranges.First();
            if (r.From.HasValue)
            {
                start = r.From.Value;
                endInclusive = r.To ?? length - 1;
            }
            else if (r.To.HasValue)
            {
                // suffix-length: 后 N 字节
                start = Math.Max(0, length - r.To.Value);
                endInclusive = length - 1;
            }

            if (start >= length || endInclusive < start)
            {
                resp.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                resp.Headers[HeaderNames.ContentRange] = $"bytes */{length}";
                return Results.Empty;
            }

            endInclusive = Math.Min(endInclusive, length - 1);
            partial = true;
        }

        var sendLength = endInclusive - start + 1;
        resp.ContentType = entry.ContentType;
        resp.ContentLength = sendLength;
        if (partial)
        {
            resp.StatusCode = StatusCodes.Status206PartialContent;
            resp.Headers[HeaderNames.ContentRange] = $"bytes {start}-{endInclusive}/{length}";
        }
        else
        {
            resp.StatusCode = StatusCodes.Status200OK;
        }

        if (HttpMethods.IsHead(req.Method))
        {
            return Results.Empty;
        }

        await using var fs = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        fs.Seek(start, SeekOrigin.Begin);

        var remaining = sendLength;
        var buffer = new byte[81920];
        var ct = req.HttpContext.RequestAborted;
        while (remaining > 0 && !ct.IsCancellationRequested)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read <= 0) break;
            await resp.Body.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }

        return Results.Empty;
    }

    private async Task<IResult> HandleSoapAsync(string service, HttpRequest httpReq)
    {
        var shortName = service.ToLowerInvariant() switch
        {
            "avtransport" => "AVTransport",
            "connectionmanager" => "ConnectionManager",
            "renderingcontrol" => "RenderingControl",
            _ => null,
        };
        if (shortName is null) return Results.NotFound();

        string body;
        using (var sr = new StreamReader(httpReq.Body, Encoding.UTF8))
            body = await sr.ReadToEndAsync().ConfigureAwait(false);

        string? actionName;
        string? serviceType;
        var args = new List<(string, string)>();
        try
        {
            var doc = XDocument.Parse(body);
            var actionEl = doc.Descendants()
                .FirstOrDefault(e => e.Parent is not null && e.Parent.Name.LocalName == "Body");
            if (actionEl is null) return SoapFault("Client", "Action not found");
            actionName = actionEl.Name.LocalName;
            serviceType = actionEl.Name.NamespaceName;
            args.AddRange(actionEl.Elements().Select(arg => (arg.Name.LocalName, arg.Value)));
        }
        catch (Exception ex)
        {
            return SoapFault("Client", $"Bad SOAP: {ex.Message}");
        }

        if (!_handlers.TryGetValue((shortName, actionName), out var handler))
        {
            log.Warn("SOAP", $"Unhandled {shortName}#{actionName}");
            return SoapFault("Client", "Action not implemented", 501, 401);
        }

        var soapReq = new SoapRequest(
            shortName,
            serviceType,
            actionName,
            args,
            httpReq.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "",
            httpReq.Headers.UserAgent.ToString(),
            httpReq.Headers["X-AV-Client-Info"].ToString());

        IReadOnlyList<(string, string)> outArgs;
        try
        {
            outArgs = await handler(soapReq).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Error("SOAP", $"{shortName}#{actionName} threw: {ex.Message}");
            return SoapFault("Server", ex.Message);
        }

        return SoapResponse(serviceType, actionName, outArgs);
    }

    private static IResult SoapResponse(string serviceType, string actionName, IReadOnlyList<(string, string)> outArgs)
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
        return Results.Content(sb.ToString(), "text/xml; charset=\"utf-8\"");
    }

    private static IResult SoapFault(string faultCode, string faultString, int httpStatus = 500, int upnpError = 501)
    {
        var xml = $"""<?xml version="1.0" encoding="utf-8"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><s:Fault><faultcode>s:{faultCode}</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>{upnpError}</errorCode><errorDescription>{WebUtility.HtmlEncode(faultString)}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>""";
        return Results.Content(xml, "text/xml; charset=\"utf-8\"", statusCode: httpStatus);
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
