using System.Threading.Channels;
using DLNAPlaylist.Core;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Cp;

/// <summary>
/// 对当前目标设备每 2s 轮询一次 GetTransportInfo + GetPositionInfo，
/// 把结果投回协调器。一期不做 GENA 事件订阅（回退到轮询）。
/// </summary>
public sealed class RemoteStatePoller : IAsyncDisposable
{
    readonly AvTransportClient _client;
    readonly ChannelWriter<CoordinatorCommand> _commands;
    readonly LogSink _log;
    readonly Func<RemoteDevice?> _currentTarget;
    readonly CancellationTokenSource _cts = new();
    Task? _loopTask;

    public RemoteStatePoller(AvTransportClient client, Func<RemoteDevice?> currentTarget,
        ChannelWriter<CoordinatorCommand> commands, LogSink log)
    {
        _client = client;
        _currentTarget = currentTarget;
        _commands = commands;
        _log = log;
    }

    public void Start()
    {
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch { break; }

            var dev = _currentTarget();
            if (dev is null) continue;

            try
            {
                var (state, _) = await _client.GetTransportInfoAsync(dev, ct).ConfigureAwait(false);
                var (pos, dur) = await _client.GetPositionInfoAsync(dev, ct).ConfigureAwait(false);
                _commands.TryWrite(new CoordinatorCommand.RemoteStateReport(state, pos, dur));
            }
            catch (Exception ex)
            {
                _log.Warn("CP", $"poll {dev.Display}: {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_loopTask is not null) await _loopTask.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
