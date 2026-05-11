namespace DLNAPlaylist.Core;

/// <summary>
///     投进 PlaybackCoordinator Channel 的命令。
/// </summary>
public abstract record CoordinatorCommand
{
    /// <summary>来自 MR 的入队请求（协调器判断是否需要审批）。</summary>
    public sealed record IncomingCast(QueueItem Item) : CoordinatorCommand;

    public sealed record ApproveCast(Guid ApprovalId, bool RememberSource) : CoordinatorCommand;

    public sealed record RejectCast(Guid ApprovalId, bool BlockSource) : CoordinatorCommand;

    public sealed record EnqueueManual(string Uri, string? Title) : CoordinatorCommand;

    public sealed record RemoveItem(Guid ItemId) : CoordinatorCommand;

    public sealed record MoveItem(Guid ItemId, int Delta) : CoordinatorCommand;

    public sealed record ClearQueue : CoordinatorCommand;

    public sealed record SetTarget(RemoteDevice? Device) : CoordinatorCommand;

    public sealed record DevicesDiscovered(IReadOnlyList<RemoteDevice> Devices) : CoordinatorCommand;

    public sealed record PlayNow : CoordinatorCommand;

    public sealed record TogglePause : CoordinatorCommand;

    public sealed record Stop : CoordinatorCommand;

    public sealed record Next : CoordinatorCommand;

    public sealed record Previous : CoordinatorCommand;

    public sealed record Seek(TimeSpan Position) : CoordinatorCommand;

    /// <summary>CP 轮询/订阅上报当前目标的状态。</summary>
    public sealed record RemoteStateReport(string TransportState, TimeSpan? Position, TimeSpan? Duration)
        : CoordinatorCommand;
}