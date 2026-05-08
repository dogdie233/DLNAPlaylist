using System.Threading.Channels;
using DLNAPlaylist.Dlna.Cp;
using DLNAPlaylist.Media;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Core;

/// <summary>
/// 中央状态机。
/// - 唯一维护：队列、当前播放项、目标设备、审批列表
/// - 唯一向电视发 SOAP 调用（通过 AvTransportClient）
/// - 收到远端状态上报后判定"播完"→ 推进下一项
///
/// 所有外部操作（MR 入队 / TUI 点击 / CP 轮询上报）都压成 CoordinatorCommand 投进 Channel，
/// 单循环消费，天然无锁。
/// </summary>
public sealed class PlaybackCoordinator : IAsyncDisposable
{
    readonly Channel<CoordinatorCommand> _commands;
    readonly EventBus<CoordinatorEvent> _bus;
    readonly LogSink _log;
    readonly AvTransportClient _cpClient;
    readonly IMediaUrlResolver _resolver;
    readonly AllowList _allowList;
    readonly CancellationTokenSource _cts = new();
    Task? _loopTask;

    // 状态
    readonly List<QueueItem> _queue = new();
    readonly List<PendingApproval> _pending = new();
    readonly List<RemoteDevice> _devices = new();
    RemoteDevice? _target;
    QueueItem? _nowPlaying;
    TimeSpan? _lastPos;
    TimeSpan? _lastDur;
    string _lastTransportState = "NO_MEDIA_PRESENT";
    bool _wasPlayingOnce;  // 用于"播完"判定：曾进入过 PLAYING 才能由 STOPPED 触发推进
    bool _isPaused;

    public PlaybackCoordinator(
        Channel<CoordinatorCommand> commands,
        EventBus<CoordinatorEvent> bus,
        LogSink log,
        AvTransportClient cpClient,
        IMediaUrlResolver resolver,
        AllowList allowList)
    {
        _commands = commands;
        _bus = bus;
        _log = log;
        _cpClient = cpClient;
        _resolver = resolver;
        _allowList = allowList;
    }

    public RemoteDevice? CurrentTarget => _target;

    public void Start()
    {
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    async Task LoopAsync(CancellationToken ct)
    {
        var reader = _commands.Reader;
        while (!ct.IsCancellationRequested)
        {
            CoordinatorCommand cmd;
            try { cmd = await reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ChannelClosedException) { break; }

            try
            {
                await HandleAsync(cmd, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Coord", $"handling {cmd.GetType().Name}: {ex.Message}");
            }
        }
    }

    async Task HandleAsync(CoordinatorCommand cmd, CancellationToken ct)
    {
        switch (cmd)
        {
            case CoordinatorCommand.IncomingCast x: HandleIncoming(x.Item); break;
            case CoordinatorCommand.ApproveCast x: HandleApprove(x.ApprovalId, x.RememberSource); break;
            case CoordinatorCommand.RejectCast x: HandleReject(x.ApprovalId, x.BlockSource); break;
            case CoordinatorCommand.EnqueueManual x: HandleEnqueueManual(x.Uri, x.Title); break;
            case CoordinatorCommand.RemoveItem x: HandleRemove(x.ItemId); break;
            case CoordinatorCommand.MoveItem x: HandleMove(x.ItemId, x.Delta); break;
            case CoordinatorCommand.ClearQueue: HandleClearQueue(); break;

            case CoordinatorCommand.DevicesDiscovered x: HandleDevicesDiscovered(x.Devices); break;
            case CoordinatorCommand.SetTarget x: await HandleSetTargetAsync(x.Device, ct); break;

            case CoordinatorCommand.PlayNow: await HandlePlayNowAsync(ct); break;
            case CoordinatorCommand.TogglePause: await HandleToggleAsync(ct); break;
            case CoordinatorCommand.Stop: await HandleStopAsync(ct); break;
            case CoordinatorCommand.Next: await HandleNextAsync(ct); break;
            case CoordinatorCommand.Previous: await HandlePreviousAsync(ct); break;
            case CoordinatorCommand.Seek x: await HandleSeekAsync(x.Position, ct); break;

            case CoordinatorCommand.RemoteStateReport x: await HandleRemoteReportAsync(x, ct); break;
        }
    }

    // ---------- 队列与审批 ----------

    void HandleIncoming(QueueItem item)
    {
        // 来源是否已被允许
        if (item.Source is { } src && _allowList.IsAllowed(src))
        {
            item.Status = QueueItemStatus.Queued;
            _queue.Add(item);
            BroadcastQueue();
            _ = TryAutoStartAsync();
            return;
        }

        // 新来源：放进审批池
        var approval = new PendingApproval(Guid.NewGuid(), item);
        _pending.Add(approval);
        BroadcastApprovals();
        _log.Info("Coord", $"待审批：{item.Title} 来自 {item.Source?.Display ?? "?"}");
    }

    void HandleApprove(Guid approvalId, bool rememberSource)
    {
        var idx = _pending.FindIndex(p => p.Id == approvalId);
        if (idx < 0) return;
        var pending = _pending[idx];
        _pending.RemoveAt(idx);
        if (rememberSource && pending.Item.Source is { } src)
            _allowList.Allow(src);

        pending.Item.Status = QueueItemStatus.Queued;
        _queue.Add(pending.Item);
        BroadcastApprovals();
        BroadcastQueue();
        _ = TryAutoStartAsync();
    }

    void HandleReject(Guid approvalId, bool blockSource)
    {
        var idx = _pending.FindIndex(p => p.Id == approvalId);
        if (idx < 0) return;
        _pending.RemoveAt(idx);
        // 一期不实现 block list，blockSource 参数暂时忽略；保留参数以兼容后续扩展
        _ = blockSource;
        BroadcastApprovals();
    }

    void HandleEnqueueManual(string uri, string? title)
    {
        var item = new QueueItem
        {
            OriginalUri = uri,
            Title = title ?? uri,
            Kind = MediaKind.Unknown,
            Source = new SourceIdentity("manual", "TUI"),
            Status = QueueItemStatus.Queued,
        };
        _queue.Add(item);
        BroadcastQueue();
        _ = TryAutoStartAsync();
    }

    void HandleRemove(Guid itemId)
    {
        var idx = _queue.FindIndex(q => q.Id == itemId);
        if (idx < 0) return;
        if (ReferenceEquals(_queue[idx], _nowPlaying))
        {
            // 删当前播放项 = 停并下一首
            _queue.RemoveAt(idx);
            _nowPlaying = null;
            BroadcastQueue();
            _ = TryAutoStartAsync();
            return;
        }
        _queue.RemoveAt(idx);
        BroadcastQueue();
    }

    void HandleMove(Guid itemId, int delta)
    {
        var idx = _queue.FindIndex(q => q.Id == itemId);
        if (idx < 0) return;
        var newIdx = Math.Clamp(idx + delta, 0, _queue.Count - 1);
        if (newIdx == idx) return;
        var item = _queue[idx];
        _queue.RemoveAt(idx);
        _queue.Insert(newIdx, item);
        BroadcastQueue();
    }

    void HandleClearQueue()
    {
        _queue.Clear();
        _nowPlaying = null;
        BroadcastQueue();
        BroadcastNowPlaying();
    }

    // ---------- 设备 ----------

    void HandleDevicesDiscovered(IReadOnlyList<RemoteDevice> devices)
    {
        _devices.Clear();
        _devices.AddRange(devices);
        _bus.Publish(new CoordinatorEvent.DevicesChanged(_devices.ToArray()));
        // 如果当前 target 还在（按 UDN），保留；不在就清空
        if (_target is not null && !_devices.Any(d => d.Udn == _target.Udn))
        {
            _target = null;
            _bus.Publish(new CoordinatorEvent.TargetChanged(null));
        }
    }

    async Task HandleSetTargetAsync(RemoteDevice? device, CancellationToken ct)
    {
        if (_target is not null && !ReferenceEquals(_target, device))
        {
            // 离开旧设备前停它
            try { await _cpClient.StopAsync(_target, ct); } catch { }
        }
        _target = device;
        _wasPlayingOnce = false;
        _isPaused = false;
        _nowPlaying = null;
        _bus.Publish(new CoordinatorEvent.TargetChanged(device));
        BroadcastNowPlaying();
        await TryAutoStartAsync(ct);
    }

    // ---------- 播放控制 ----------

    async Task HandlePlayNowAsync(CancellationToken ct)
    {
        if (_nowPlaying is not null && _target is not null)
        {
            try { await _cpClient.PlayAsync(_target, ct); _isPaused = false; BroadcastNowPlaying(); }
            catch (Exception ex) { _log.Warn("Coord", $"play: {ex.Message}"); }
            return;
        }
        await TryAutoStartAsync(ct);
    }

    async Task HandleToggleAsync(CancellationToken ct)
    {
        if (_nowPlaying is null || _target is null) return;
        try
        {
            if (_isPaused) { await _cpClient.PlayAsync(_target, ct); _isPaused = false; }
            else           { await _cpClient.PauseAsync(_target, ct); _isPaused = true; }
            BroadcastNowPlaying();
        }
        catch (Exception ex) { _log.Warn("Coord", $"toggle: {ex.Message}"); }
    }

    async Task HandleStopAsync(CancellationToken ct)
    {
        if (_target is null) return;
        try { await _cpClient.StopAsync(_target, ct); } catch { }
        _nowPlaying = null;
        _isPaused = false;
        _wasPlayingOnce = false;
        BroadcastNowPlaying();
    }

    async Task HandleNextAsync(CancellationToken ct)
    {
        // 弹出当前播放（若在队列首）并起下一项
        if (_nowPlaying is not null)
        {
            _queue.RemoveAll(q => q.Id == _nowPlaying.Id);
            _nowPlaying = null;
        }
        BroadcastQueue();
        if (_target is not null) { try { await _cpClient.StopAsync(_target, ct); } catch { } }
        _wasPlayingOnce = false;
        await TryAutoStartAsync(ct);
    }

    Task HandlePreviousAsync(CancellationToken ct)
    {
        // "上一首"语义：重新从头播当前项
        if (_nowPlaying is not null && _target is not null)
        {
            try { return _cpClient.SeekAsync(_target, TimeSpan.Zero, ct); }
            catch { }
        }
        return Task.CompletedTask;
    }

    async Task HandleSeekAsync(TimeSpan position, CancellationToken ct)
    {
        if (_target is null) return;
        try { await _cpClient.SeekAsync(_target, position, ct); }
        catch (Exception ex) { _log.Warn("Coord", $"seek: {ex.Message}"); }
    }

    // ---------- 远端状态上报 ----------

    async Task HandleRemoteReportAsync(CoordinatorCommand.RemoteStateReport x, CancellationToken ct)
    {
        _lastPos = x.Position;
        _lastDur = x.Duration;
        var previous = _lastTransportState;
        _lastTransportState = x.TransportState;
        if (x.TransportState == "PLAYING") _wasPlayingOnce = true;

        // 判定"播完"：之前进过 PLAYING，现在是 STOPPED 或 NO_MEDIA_PRESENT，且(已知时长时)位置接近时长 / 或位置为 0 都可接受
        var finished =
            _wasPlayingOnce &&
            previous is "PLAYING" or "TRANSITIONING" or "PAUSED_PLAYBACK" &&
            (x.TransportState is "STOPPED" or "NO_MEDIA_PRESENT");

        if (finished && _nowPlaying is not null)
        {
            _log.Info("Coord", $"判定播完：{_nowPlaying.Title}");
            _nowPlaying.Status = QueueItemStatus.Done;
            _queue.RemoveAll(q => q.Id == _nowPlaying.Id);
            _nowPlaying = null;
            _wasPlayingOnce = false;
            BroadcastQueue();
            await TryAutoStartAsync(ct);
        }

        BroadcastNowPlaying();
    }

    // ---------- 自动起播 ----------

    Task TryAutoStartAsync() => TryAutoStartAsync(CancellationToken.None);

    async Task TryAutoStartAsync(CancellationToken ct)
    {
        if (_nowPlaying is not null) return;
        if (_target is null) return;
        var next = _queue.FirstOrDefault(q => q.Status == QueueItemStatus.Queued);
        if (next is null) return;

        try
        {
            var resolved = await _resolver.ResolveAsync(next, _target, ct).ConfigureAwait(false);
            await _cpClient.SetUriAsync(_target, resolved.Uri, resolved.DidlLite, ct).ConfigureAwait(false);
            await _cpClient.PlayAsync(_target, ct).ConfigureAwait(false);
            next.Status = QueueItemStatus.Playing;
            _nowPlaying = next;
            _isPaused = false;
            _wasPlayingOnce = false;
            _log.Info("Coord", $"开始播放：{next.Title} → {_target.Display}");
            BroadcastQueue();
            BroadcastNowPlaying();
        }
        catch (Exception ex)
        {
            next.Status = QueueItemStatus.Failed;
            next.LastError = ex.Message;
            _log.Error("Coord", $"起播失败：{next.Title}: {ex.Message}");
            BroadcastQueue();
        }
    }

    // ---------- 广播 ----------

    void BroadcastQueue() => _bus.Publish(new CoordinatorEvent.QueueChanged(_queue.ToArray()));
    void BroadcastApprovals() => _bus.Publish(new CoordinatorEvent.ApprovalsChanged(_pending.ToArray()));
    void BroadcastNowPlaying() =>
        _bus.Publish(new CoordinatorEvent.NowPlayingChanged(_nowPlaying, _lastPos, _lastDur ?? _nowPlaying?.Duration, _isPaused));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _commands.Writer.TryComplete();
        try { if (_loopTask is not null) await _loopTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
