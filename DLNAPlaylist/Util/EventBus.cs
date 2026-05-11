using System.Threading.Channels;

using DLNAPlaylist.Core;

namespace DLNAPlaylist.Util;

/// <summary>
///     一个极简事件总线。多订阅者，每人独立 channel，慢的订阅者不拖慢别人。
///     事件用 record，线程安全。
/// </summary>
public sealed class EventBus<T> where T : class
{
    private readonly Lock _lock = new();
    private readonly List<ChannelWriter<T>> _subs = [];

    public ChannelReader<T> Subscribe()
    {
        var ch = Channel.CreateBounded<T>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        lock (_lock)
        {
            _subs.Add(ch.Writer);
        }

        return ch.Reader;
    }

    public void Publish(T evt)
    {
        List<ChannelWriter<T>> snapshot;
        lock (_lock)
        {
            snapshot = [.._subs];
        }

        foreach (var w in snapshot)
            // 丢到每个订阅者的 channel；满了靠 DropOldest
            _ = w.TryWrite(evt);
    }
}

/// <summary>
///     写日志的同时广播给订阅者。
/// </summary>
public sealed class LogSink
{
    private readonly EventBus<CoordinatorEvent> _bus;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly List<LogEntry> _tail = [];

    public LogSink(EventBus<CoordinatorEvent> bus, int capacity = 500)
    {
        _bus = bus;
        _capacity = capacity;
    }

    public void Info(string category, string message)
    {
        Append(LogLevel.Information, category, message);
    }

    public void Warn(string category, string message)
    {
        Append(LogLevel.Warning, category, message);
    }

    public void Error(string category, string message)
    {
        Append(LogLevel.Error, category, message);
    }

    /// <summary>由 ILogger 桥进来用的；外部调用建议用 Info/Warn/Error。</summary>
    public void Log(LogLevel level, string category, string message)
    {
        Append(level, category, message);
    }

    private void Append(LogLevel lvl, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, lvl, category, message);
        lock (_lock)
        {
            _tail.Add(entry);
            if (_tail.Count > _capacity) _tail.RemoveRange(0, _tail.Count - _capacity);
        }

        _bus.Publish(new CoordinatorEvent.LogAppended(entry));
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return _tail.ToArray();
        }
    }
}