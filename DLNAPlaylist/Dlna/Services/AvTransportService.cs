using System.Threading.Channels;
using DLNAPlaylist.Core;
using DLNAPlaylist.Dlna.Http;
using DLNAPlaylist.Media;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Services;

/// <summary>
/// MR 端 AVTransport 的最小实现。
///
/// 关键点：SetAVTransportURI 收到后立即 200 返回，
/// 把 QueueItem 投进 coordinator 的命令 channel 让它异步处理（审批/入队）。
/// 不能在这里同步等人工确认 —— CP 会超时。
/// </summary>
public sealed class AvTransportService
{
    readonly ChannelWriter<CoordinatorCommand> _commands;
    readonly LogSink _log;

    public AvTransportService(ChannelWriter<CoordinatorCommand> commands, LogSink log)
    {
        _commands = commands;
        _log = log;
    }

    public void RegisterTo(DeviceHttpServer server)
    {
        server.Register("AVTransport", "SetAVTransportURI", HandleSetUriAsync);
        server.Register("AVTransport", "Play", HandlePlayAsync);
        server.Register("AVTransport", "Pause", HandlePauseAsync);
        server.Register("AVTransport", "Stop", HandleStopAsync);
        server.Register("AVTransport", "GetTransportInfo", HandleGetTransportInfoAsync);
        server.Register("AVTransport", "GetPositionInfo", HandleGetPositionInfoAsync);
    }

    Task<IReadOnlyList<(string, string)>> HandleSetUriAsync(SoapRequest req)
    {
        var uri = req.Get("CurrentURI") ?? "";
        var didl = req.Get("CurrentURIMetaData");

        if (string.IsNullOrWhiteSpace(uri))
        {
            _log.Warn("MR", $"SetAVTransportURI 缺 URI，忽略");
            return EmptyOk();
        }

        var info = DidlParser.Parse(didl, fallbackTitle: TitleFromUri(uri));
        if (info.Kind == MediaKind.Image)
        {
            _log.Info("MR", $"拒绝图片投屏：{info.Title}");
            // 返回成功避免 CP 重试；队列里不加
            return EmptyOk();
        }

        var source = new SourceIdentity(
            Ip: req.RemoteIp,
            UserAgent: string.IsNullOrWhiteSpace(req.XAvClientInfo) ? req.UserAgent : req.XAvClientInfo);

        var item = new QueueItem
        {
            OriginalUri = uri,
            Title = string.IsNullOrWhiteSpace(info.Title) ? TitleFromUri(uri) : info.Title,
            OriginalDidlLite = didl,
            Duration = info.Duration,
            Kind = info.Kind == MediaKind.Unknown ? MediaKind.Video : info.Kind,
            Source = source,
        };

        _commands.TryWrite(new CoordinatorCommand.IncomingCast(item));
        _log.Info("MR", $"收到投屏：{item.Title} 来自 {source.Display}");
        return EmptyOk();
    }

    // Play/Pause/Stop：MR 层面我们没有真实播放器，把命令转给协调器（控制当前目标设备）
    Task<IReadOnlyList<(string, string)>> HandlePlayAsync(SoapRequest req)
    {
        _commands.TryWrite(new CoordinatorCommand.PlayNow());
        return EmptyOk();
    }
    Task<IReadOnlyList<(string, string)>> HandlePauseAsync(SoapRequest req)
    {
        _commands.TryWrite(new CoordinatorCommand.TogglePause());
        return EmptyOk();
    }
    Task<IReadOnlyList<(string, string)>> HandleStopAsync(SoapRequest req)
    {
        _commands.TryWrite(new CoordinatorCommand.Stop());
        return EmptyOk();
    }

    Task<IReadOnlyList<(string, string)>> HandleGetTransportInfoAsync(SoapRequest req)
    {
        // 极简：永远声明 STOPPED，反正 CP 往往也不查
        IReadOnlyList<(string, string)> outArgs = new (string, string)[]
        {
            ("CurrentTransportState", "STOPPED"),
            ("CurrentTransportStatus", "OK"),
            ("CurrentSpeed", "1"),
        };
        return Task.FromResult(outArgs);
    }

    Task<IReadOnlyList<(string, string)>> HandleGetPositionInfoAsync(SoapRequest req)
    {
        IReadOnlyList<(string, string)> outArgs = new (string, string)[]
        {
            ("Track", "0"),
            ("TrackDuration", "00:00:00"),
            ("TrackMetaData", ""),
            ("TrackURI", ""),
            ("RelTime", "00:00:00"),
            ("AbsTime", "00:00:00"),
            ("RelCount", "0"),
            ("AbsCount", "0"),
        };
        return Task.FromResult(outArgs);
    }

    static Task<IReadOnlyList<(string, string)>> EmptyOk()
    {
        IReadOnlyList<(string, string)> o = Array.Empty<(string, string)>();
        return Task.FromResult(o);
    }

    static string TitleFromUri(string uri)
    {
        try
        {
            var u = new Uri(uri, UriKind.RelativeOrAbsolute);
            var name = u.IsAbsoluteUri ? Path.GetFileName(u.LocalPath) : uri;
            return string.IsNullOrWhiteSpace(name) ? uri : Uri.UnescapeDataString(name);
        }
        catch { return uri; }
    }
}
