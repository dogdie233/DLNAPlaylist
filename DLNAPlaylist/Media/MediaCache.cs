using System.Collections.Concurrent;

using DLNAPlaylist.Core;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Media;

/// <summary>
///     本地媒体缓存。
///     - 一个 QueueItem.Id 对应一个缓存条目，原始流整个下载到磁盘（边下边等）
///     - 同 itemId 的并发请求共享一次下载（用 Lazy&lt;Task&gt;）
///     - 进程退出时整个缓存目录擦掉
///     一期为简化先做"下载完再起播"，避免边下边播带来的 Range / 断流复杂度。
///     电视吃到的就是本机的稳定文件，比临时流可靠得多。
/// </summary>
public sealed class MediaCache : IAsyncDisposable
{
    private readonly LogSink _log;
    private readonly string _root;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<Entry>>> _entries = new();
    private readonly HttpClient _http;

    public MediaCache(LogSink log)
    {
        _log = log;
        _root = Path.Combine(Path.GetTempPath(), "dlnaplaylist-cache", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // 没必要复用全局 HttpClient：缓存任务并发量低，按需独占一份，超时给宽点
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // 流可能很大；不要让 HttpClient 把 body 限时
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _log.Info("Cache", $"缓存目录：{_root}");
    }

    /// <summary>查表：拿到缓存条目，没有就触发下载。同 itemId 的并发只下一次。</summary>
    public Task<Entry> GetOrFetchAsync(QueueItem item, CancellationToken ct)
    {
        var lazy = _entries.GetOrAdd(item.Id, _ => new Lazy<Task<Entry>>(
            () => DownloadAsync(item, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    /// <summary>已经缓存过的条目按 id 取出来，给 HTTP endpoint 流式回送。</summary>
    public bool TryGet(Guid id, out Entry entry)
    {
        if (_entries.TryGetValue(id, out var lazy)
            && lazy.IsValueCreated
            && lazy.Value is { IsCompletedSuccessfully: true } t)
        {
            entry = t.Result;
            return true;
        }

        entry = default!;
        return false;
    }

    private async Task<Entry> DownloadAsync(QueueItem item, CancellationToken ct)
    {
        var fileName = ChooseFileName(item);
        var path = Path.Combine(_root, item.Id.ToString("N") + "_" + fileName);

        _log.Info("Cache", $"开始下载：{item.OriginalUri} → {path}");

        using var req = new HttpRequestMessage(HttpMethod.Get, item.OriginalUri);
        // 投屏 app 内嵌服务一般不挑 UA，但带一个不会错
        req.Headers.UserAgent.ParseAdd("DLNAPlaylist/1.0");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.ToString();
        var contentLength = resp.Content.Headers.ContentLength;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
        {
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        var info = new FileInfo(path);
        if (contentLength.HasValue && info.Length != contentLength.Value)
        {
            _log.Warn("Cache", $"下载长度对不上：实际 {info.Length} vs Content-Length {contentLength}（继续，但电视可能会卡）");
        }

        _log.Info("Cache", $"下载完成：{item.Title}（{info.Length / 1024} KB）");

        return new Entry(item.Id, fileName, path, info.Length, contentType ?? GuessContentType(fileName));
    }

    private static string ChooseFileName(QueueItem item)
    {
        try
        {
            var u = new Uri(item.OriginalUri, UriKind.Absolute);
            var leaf = Path.GetFileName(u.LocalPath);
            if (!string.IsNullOrWhiteSpace(leaf))
            {
                return SanitizeName(Uri.UnescapeDataString(leaf));
            }
        }
        catch
        {
            // 忽略
        }

        return "stream.bin";
    }

    private static string SanitizeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            buf[i] = Array.IndexOf(bad, name[i]) >= 0 ? '_' : name[i];
        }
        return new string(buf);
    }

    private static string GuessContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".ts" => "video/mp2t",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".wav" => "audio/wav",
            ".aac" => "audio/aac",
            _ => "application/octet-stream",
        };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
                _log.Info("Cache", $"已清理缓存目录：{_root}");
            }
        }
        catch (Exception ex)
        {
            _log.Warn("Cache", $"清理缓存目录失败：{ex.Message}");
        }

        await Task.CompletedTask;
    }

    public sealed record Entry(Guid Id, string FileName, string Path, long Length, string ContentType);
}
