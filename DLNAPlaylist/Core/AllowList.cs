using System.Collections.Concurrent;

namespace DLNAPlaylist.Core;

/// <summary>
///     会话内的来源白名单。内存存储，进程退出即丢。
///     线程安全：MR 收 SOAP 在任意线程。
/// </summary>
public sealed class AllowList
{
    private readonly ConcurrentDictionary<SourceIdentity, bool> _allowed = new();

    public bool IsAllowed(SourceIdentity id)
    {
        return _allowed.ContainsKey(id);
    }

    public void Allow(SourceIdentity id)
    {
        _allowed[id] = true;
    }

    public void Revoke(SourceIdentity id)
    {
        _allowed.TryRemove(id, out _);
    }

    public IReadOnlyCollection<SourceIdentity> Snapshot()
    {
        return _allowed.Keys.ToArray();
    }
}