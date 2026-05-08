using DLNAPlaylist.Core;

namespace DLNAPlaylist.Media;

/// <summary>
/// 把队列项最终要交给电视的 URL / DIDL 解析出来。
/// 一期：PassthroughResolver 直接返回原 URL。
/// 二期：可替换成代理实现（起本地 HTTP，重写 URL / DIDL res@）。
/// </summary>
public interface IMediaUrlResolver
{
    ValueTask<ResolvedMedia> ResolveAsync(QueueItem item, RemoteDevice target, CancellationToken ct);
}

public sealed record ResolvedMedia(string Uri, string DidlLite);

public sealed class PassthroughMediaUrlResolver : IMediaUrlResolver
{
    public ValueTask<ResolvedMedia> ResolveAsync(QueueItem item, RemoteDevice target, CancellationToken ct)
    {
        var didl = item.OriginalDidlLite ?? DidlBuilder.Minimal(item);
        return new ValueTask<ResolvedMedia>(new ResolvedMedia(item.OriginalUri, didl));
    }
}
