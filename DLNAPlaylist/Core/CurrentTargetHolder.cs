namespace DLNAPlaylist.Core;

/// <summary>
///     当前目标 MR 的全局可见状态（线程安全）。
///     专门拆出来打破 PlaybackCoordinator ↔ RemoteStatePoller 的双向依赖：
///     coordinator 写，poller 读。其他人也读。
/// </summary>
public sealed class CurrentTargetHolder
{
    private RemoteDevice? _target;

    public RemoteDevice? Current => Volatile.Read(ref _target);

    public void Set(RemoteDevice? device) => Volatile.Write(ref _target, device);
}
