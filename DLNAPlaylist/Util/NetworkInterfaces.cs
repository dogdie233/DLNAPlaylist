using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net;

namespace DLNAPlaylist.Util;

/// <summary>
/// 选网卡 / 拿对外可达 IP。
/// 原则：优先"up + 非回环 + 非虚拟 + 有 IPv4 的网卡"的第一张。
/// </summary>
public static class NetworkInterfaces
{
    /// <summary>
    /// 返回可用于 SSDP 多播和 HTTP 监听的本机 IPv4 地址。
    /// </summary>
    public static IPAddress PickPrimaryIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            // 粗略过滤虚拟网卡
            var desc = ni.Description.ToLowerInvariant();
            if (desc.Contains("virtual") || desc.Contains("pseudo") || desc.Contains("vethernet")) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address;
                }
            }
        }
        // 兜底
        return IPAddress.Loopback;
    }

    /// <summary>
    /// 所有 up 的 IPv4 网卡地址，用于 SSDP 多播加入。
    /// </summary>
    public static IReadOnlyList<IPAddress> AllUpIPv4()
    {
        var list = new List<IPAddress>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(addr.Address);
            }
        }
        return list;
    }
}
