namespace DLNAPlaylist.Dlna;

/// <summary>
/// 全局常量。UUID 每次进程启动随机生成（不持久化即决策）。
/// </summary>
public static class DlnaConstants
{
    public const string SsdpMulticastAddr = "239.255.255.250";
    public const int SsdpPort = 1900;
    public const string SsdpUserAgent = "UPnP/1.0 DLNAPlaylist/1.0";
    public const string ServerHeader = "DLNAPlaylist/1.0 UPnP/1.0";

    public const string DeviceTypeMediaRenderer = "urn:schemas-upnp-org:device:MediaRenderer:1";

    public const string ServiceTypeAvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string ServiceTypeConnectionManager = "urn:schemas-upnp-org:service:ConnectionManager:1";
    public const string ServiceTypeRenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";

    public const string ServiceIdAvTransport = "urn:upnp-org:serviceId:AVTransport";
    public const string ServiceIdConnectionManager = "urn:upnp-org:serviceId:ConnectionManager";
    public const string ServiceIdRenderingControl = "urn:upnp-org:serviceId:RenderingControl";
}
