namespace DLNAPlaylist.Core;

/// <summary>
///     一个投屏来源的身份标识。用于会话内白名单去重。
/// </summary>
public sealed record SourceIdentity(string Ip, string UserAgent)
{
    public string Display => string.IsNullOrEmpty(UserAgent) ? Ip : $"{Ip} ({UserAgent})";
}