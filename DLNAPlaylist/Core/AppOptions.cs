using System.Net;

namespace DLNAPlaylist.Core;

/// <summary>
///     启动期决定的不可变配置：网卡、端口、UDN、设备友好名。
///     CLI 解析完成后注入 DI 容器，所有组件按需读取。
/// </summary>
public sealed class AppOptions
{
    public required IPAddress BindAddress { get; init; }
    public required int HttpPort { get; init; }
    public required string Udn { get; init; }
    public required string FriendlyName { get; init; }

    public string LocationUrl => $"http://{BindAddress}:{HttpPort}/device.xml";
}
