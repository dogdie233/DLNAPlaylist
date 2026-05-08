namespace DLNAPlaylist.Core;

public enum QueueItemStatus
{
    PendingApproval,
    Queued,
    Playing,
    Paused,
    Done,
    Failed,
}

/// <summary>
/// 队列中的一项。Id 是会话内唯一，不持久化。
/// </summary>
public sealed class QueueItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string OriginalUri { get; init; }
    public required string Title { get; init; }
    public string? OriginalDidlLite { get; init; }
    public TimeSpan? Duration { get; init; }
    public MediaKind Kind { get; init; } = MediaKind.Unknown;
    public SourceIdentity? Source { get; init; }
    public QueueItemStatus Status { get; set; } = QueueItemStatus.PendingApproval;
    public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
    public string? LastError { get; set; }
}
