using System.Threading.Channels;

using DLNAPlaylist.Core;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Cp;

/// <summary>
///     对当前目标设备每 2s 轮询一次 GetTransportInfo + GetPositionInfo，
///     把结果投回协调器。一期不做 GENA 事件订阅（回退到轮询）。
/// </summary>
public sealed class RemoteStatePoller(
    AvTransportClient client,
    CurrentTargetHolder target,
    ChannelWriter<CoordinatorCommand> commands,
    LogSink log)
    : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            if (_loopTask is not null) await _loopTask.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            var dev = target.Current;
            if (dev is null) continue;

            try
            {
                var (state, _) = await client.GetTransportInfoAsync(dev, ct).ConfigureAwait(false);
                var (pos, dur) = await client.GetPositionInfoAsync(dev, ct).ConfigureAwait(false);
                commands.TryWrite(new CoordinatorCommand.RemoteStateReport(state, pos, dur));
            }
            catch (Exception ex)
            {
                log.Warn("CP", $"poll {dev.Display}: {ex.Message}");
            }
        }
    }
}
