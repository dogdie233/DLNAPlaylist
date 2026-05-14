using System.Net;
using System.Xml.Linq;

using DLNAPlaylist.Core;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Media;

/// <summary>
///     检测"投屏 App 自带临时流服务器"的 resolver：
///     如果 OriginalUri 的 host 跟投送者 IP 是同一个，那这个 URL 大概率
///     是投屏 App（手机/电脑端）自己起的内嵌 HTTP 流服务，cast 命令一发完
///     就可能被关掉/前台杀死。这种情况先把流整个拉到本地缓存，
///     然后把交给电视的 URL 改写成本机 /cache/&lt;id&gt;/&lt;name&gt;。
///
///     不命中（比如就是 NAS / B 站直链 / IPTV 等真·公网源）就直接 passthrough，
///     不要无谓增加延迟。
///
///     失败兜底：缓存任何环节炸了都退回原 URL，让电视自己试一次，至少别比 passthrough 还差。
/// </summary>
public sealed class LocalCachingMediaUrlResolver : IMediaUrlResolver
{
    private static readonly XNamespace DidlNs = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";

    private readonly AppOptions _opts;
    private readonly LogSink _log;
    private readonly MediaCache _cache;

    public LocalCachingMediaUrlResolver(AppOptions opts, LogSink log, MediaCache cache)
    {
        _opts = opts;
        _log = log;
        _cache = cache;
    }

    public async ValueTask<ResolvedMedia> ResolveAsync(QueueItem item, RemoteDevice target, CancellationToken ct)
    {
        var didl = item.OriginalDidlLite ?? DidlBuilder.Minimal(item);

        if (!ShouldCache(item, out var reason))
        {
            return new ResolvedMedia(item.OriginalUri, didl);
        }

        _log.Info("Cache", $"判定为投屏端临时流（{reason}），先缓存：{item.Title}");

        try
        {
            var entry = await _cache.GetOrFetchAsync(item, ct).ConfigureAwait(false);
            var rewrittenUri = BuildLocalUrl(entry);
            var rewrittenDidl = RewriteDidlUri(didl, rewrittenUri);
            _log.Info("Cache", $"缓存就绪：{item.Title} → {rewrittenUri}");
            return new ResolvedMedia(rewrittenUri, rewrittenDidl);
        }
        catch (Exception ex)
        {
            _log.Warn("Cache", $"缓存失败，回退到原 URL：{item.Title}: {ex.Message}");
            return new ResolvedMedia(item.OriginalUri, didl);
        }
    }

    private static bool ShouldCache(QueueItem item, out string reason)
    {
        reason = "";
        if (item.Source is null)
        {
            return false;
        }

        if (!Uri.TryCreate(item.OriginalUri, UriKind.Absolute, out var u))
        {
            return false;
        }

        // 只处理 http/https；其它 scheme（rtsp/rtmp 之类）我们没法本地代理
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!HostsEquivalent(u, item.Source.Ip))
        {
            return false;
        }

        reason = $"URL host={u.Host} 与来源 IP={item.Source.Ip} 一致";
        return true;
    }

    /// <summary>
    ///     比较 URL host 与来源 IP 是否同一台机器。
    ///     - URL host 可能带 [v6]、可能是域名（极少见的"投屏 app 给自己起了个 mDNS 名"也算同机，
    ///       但我们不主动解析 DNS，只比 IP 字面）
    ///     - source IP 在 IPv4-mapped IPv6（::ffff:1.2.3.4）形式时也要等价
    /// </summary>
    private static bool HostsEquivalent(Uri url, string sourceIp)
    {
        if (url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return false;
        }

        if (!IPAddress.TryParse(url.Host, out var urlIp))
        {
            return false;
        }

        if (!IPAddress.TryParse(sourceIp, out var srcIp))
        {
            return false;
        }

        // 把 IPv4-mapped IPv6 还原成 IPv4 再比，避免 ::ffff:192.168.1.5 vs 192.168.1.5 比不上
        if (urlIp.IsIPv4MappedToIPv6) urlIp = urlIp.MapToIPv4();
        if (srcIp.IsIPv4MappedToIPv6) srcIp = srcIp.MapToIPv4();

        return urlIp.Equals(srcIp);
    }

    private string BuildLocalUrl(MediaCache.Entry entry)
    {
        // 文件名里的字符走 EscapeDataString，避免空格/中文/特殊符号砸了 URL
        var name = Uri.EscapeDataString(entry.FileName);
        return $"http://{_opts.BindAddress}:{_opts.HttpPort}/cache/{entry.Id:N}/{name}";
    }

    /// <summary>
    ///     把 DIDL-Lite 里所有 res 节点的 URL 改成本地 URL，让电视真正去拉本机。
    ///     不动其它字段（标题、protocolInfo、duration 等保留，CP 端电视依靠这些识别码率/类型）。
    /// </summary>
    private static string RewriteDidlUri(string didl, string newUri)
    {
        try
        {
            var doc = XDocument.Parse(didl);
            var resNodes = doc.Descendants()
                .Where(e => e.Name.LocalName == "res")
                .ToArray();
            if (resNodes.Length == 0)
            {
                // 没有 res 节点就保留原样，反正 CP 真正看的是 SetAVTransportURI 的 CurrentURI
                return didl;
            }

            foreach (var res in resNodes)
            {
                res.Value = newUri;
            }

            // 不写 XML 声明：DLNA CP 大多吃 inline DIDL，带不带 prolog 都能解
            return doc.Root?.ToString(SaveOptions.DisableFormatting) ?? didl;
        }
        catch
        {
            // 解析失败就别瞎改，原样回
            return didl;
        }
    }
}
