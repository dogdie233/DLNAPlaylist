using DLNAPlaylist.Core;
using System.Net;
using System.Xml.Linq;

namespace DLNAPlaylist.Media;

/// <summary>
/// 极简 DIDL-Lite 工具：解析（拿标题/时长/protocolInfo/upnp:class）、
/// 以及在外部 URL 没带元数据时合成一个最小可用的 DIDL-Lite。
/// </summary>
public static class DidlParser
{
    static readonly XNamespace DidlNs = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    static readonly XNamespace DcNs   = "http://purl.org/dc/elements/1.1/";
    static readonly XNamespace UpnpNs = "urn:schemas-upnp-org:metadata-1-0/upnp/";

    public sealed record DidlInfo(string Title, TimeSpan? Duration, MediaKind Kind, string? ProtocolInfo);

    public static DidlInfo Parse(string? didl, string fallbackTitle)
    {
        if (string.IsNullOrWhiteSpace(didl))
            return new DidlInfo(fallbackTitle, null, MediaKind.Unknown, null);

        try
        {
            var doc = XDocument.Parse(didl);
            var item = doc.Descendants(DidlNs + "item").FirstOrDefault()
                    ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "item");
            if (item is null) return new DidlInfo(fallbackTitle, null, MediaKind.Unknown, null);

            var title = (string?)item.Element(DcNs + "title")
                     ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value
                     ?? fallbackTitle;

            var upnpClass = (string?)item.Element(UpnpNs + "class")
                          ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "class")?.Value
                          ?? "";

            var kind = ClassifyUpnpClass(upnpClass);

            var res = item.Elements(DidlNs + "res").FirstOrDefault()
                   ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "res");
            string? protocolInfo = null;
            TimeSpan? duration = null;
            if (res is not null)
            {
                protocolInfo = (string?)res.Attribute("protocolInfo");
                var durAttr = (string?)res.Attribute("duration");
                duration = Util.DlnaTime.TryParse(durAttr);
                if (kind == MediaKind.Unknown && protocolInfo is not null)
                    kind = ClassifyProtocolInfo(protocolInfo);
            }

            return new DidlInfo(title, duration, kind, protocolInfo);
        }
        catch
        {
            return new DidlInfo(fallbackTitle, null, MediaKind.Unknown, null);
        }
    }

    static MediaKind ClassifyUpnpClass(string upnpClass)
    {
        upnpClass = upnpClass.ToLowerInvariant();
        if (upnpClass.Contains("videoitem")) return MediaKind.Video;
        if (upnpClass.Contains("audioitem") || upnpClass.Contains("musictrack")) return MediaKind.Audio;
        if (upnpClass.Contains("imageitem") || upnpClass.Contains("photo")) return MediaKind.Image;
        return MediaKind.Unknown;
    }

    static MediaKind ClassifyProtocolInfo(string protocolInfo)
    {
        var lower = protocolInfo.ToLowerInvariant();
        if (lower.Contains(":video/")) return MediaKind.Video;
        if (lower.Contains(":audio/")) return MediaKind.Audio;
        if (lower.Contains(":image/")) return MediaKind.Image;
        return MediaKind.Unknown;
    }
}

public static class DidlBuilder
{
    /// <summary>
    /// 当投来的请求没带 DIDL-Lite 时，我们造一个最小能被目标设备接受的。
    /// </summary>
    public static string Minimal(QueueItem item)
    {
        var (upnpClass, mime) = item.Kind switch
        {
            MediaKind.Audio => ("object.item.audioItem.musicTrack", "audio/*"),
            MediaKind.Image => ("object.item.imageItem.photo", "image/*"),
            _               => ("object.item.videoItem", "video/*"),
        };
        var title = WebUtility.HtmlEncode(item.Title);
        var uri   = WebUtility.HtmlEncode(item.OriginalUri);
        var protocolInfo = $"http-get:*:{mime}:*";

        return $"""<?xml version="1.0" encoding="utf-8"?><DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"><item id="1" parentID="0" restricted="1"><dc:title>{title}</dc:title><upnp:class>{upnpClass}</upnp:class><res protocolInfo="{protocolInfo}">{uri}</res></item></DIDL-Lite>""";
    }
}
