namespace DLNAPlaylist.Core;

/// <summary>
/// 协调器对外广播的事件（TUI 订阅后刷新）。
/// </summary>
public abstract record CoordinatorEvent
{
    public sealed record QueueChanged(IReadOnlyList<QueueItem> Items) : CoordinatorEvent;
    public sealed record TargetChanged(RemoteDevice? Target) : CoordinatorEvent;
    public sealed record DevicesChanged(IReadOnlyList<RemoteDevice> Devices) : CoordinatorEvent;
    public sealed record NowPlayingChanged(QueueItem? Item, TimeSpan? Position, TimeSpan? Duration, bool IsPaused) : CoordinatorEvent;
    public sealed record ApprovalsChanged(IReadOnlyList<PendingApproval> Pending) : CoordinatorEvent;
    public sealed record LogAppended(LogEntry Entry) : CoordinatorEvent;
}

public sealed record PendingApproval(Guid Id, QueueItem Item);

public enum LogLevel
{
    Info,
    Warn,
    Error,
}

public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Category, string Message);
