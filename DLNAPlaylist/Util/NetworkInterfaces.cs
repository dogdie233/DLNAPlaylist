using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DLNAPlaylist.Util;

/// <summary>
///     选网卡 / 拿对外可达 IP。
///     选择策略（按优先级）：
///     1. 排除：未 Up / Loopback / Tunnel / 名字像 OpenVPN/TAP/VPN/VMware/Hyper-V/Docker/WSL 的
///     2. 优先：有默认网关的 IPv4 网卡（家庭 LAN 必然有，VPN 通常也有所以仅作弱信号）
///     3. 在剩余候选中，优先：私有网段 (192.168.* / 10.* / 172.16-31.*) 中的"非 OpenVPN 段"
///     4. 同等情况下，物理类型 (Ethernet/Wireless) 优先于其他
/// </summary>
public static class NetworkInterfaces
{
    /// <summary>
    ///     列出所有候选，附带评分和虚拟标记。仅用于诊断/调试。
    /// </summary>
    public static IReadOnlyList<Candidate> EnumerateCandidates()
    {
        var list = new List<Candidate>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            var ipProps = ni.GetIPProperties();
            var hasGw = ipProps.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !g.Address.Equals(IPAddress.Any));
            var looksVirtual = LooksLikeVirtualNic(ni);

            foreach (var addr in ipProps.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(addr.Address)) continue;
                if (addr.Address.ToString().StartsWith("169.254.")) continue; // APIPA

                var score = 0;
                if (!looksVirtual) score += 100;
                if (hasGw) score += 30;
                if (IsPrivateLan(addr.Address)) score += 20;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                    score += 10;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Ppp) score -= 50; // 拨号 / VPN 常被识别成 PPP

                list.Add(new Candidate(ni, addr.Address, hasGw, looksVirtual, score));
            }
        }

        return list.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>
    ///     自动选定一张主网卡的 IPv4 地址。
    /// </summary>
    public static IPAddress PickPrimaryIPv4()
    {
        var best = EnumerateCandidates().FirstOrDefault();
        return best?.Address ?? IPAddress.Loopback;
    }

    private static bool IsPrivateLan(IPAddress addr)
    {
        var b = addr.GetAddressBytes();
        if (b[0] == 10) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        return false;
    }

    private static bool LooksLikeVirtualNic(NetworkInterface ni)
    {
        // 综合 Name + Description 一起匹配；OpenVPN 在不同驱动下有 TAP-Windows / OpenVPN Data Channel Offload / Wintun
        var name = ni.Name.ToLowerInvariant();
        var desc = ni.Description.ToLowerInvariant();
        var combined = name + " | " + desc;

        string[] signals =
        [
            "openvpn", "tap-windows", "tap windows", "wintun", "mihomo",
            "wireguard", "tailscale", "zerotier",
            "vmware", "vethernet", "virtualbox", "vbox",
            "hyper-v", "hyperv",
            "docker", "wsl", "containers",
            "loopback", "pseudo-interface", "isatap", "teredo",
            "virtual", "virtual ethernet", "virtual adapter",
            "miniport" // 但有些真实网卡也带 miniport，不要单独触发
        ];

        // 必须命中"虚拟/隧道"明确关键字之一才判定虚拟；单独的 "miniport" 太宽，跳过
        foreach (var s in signals)
        {
            if (s == "miniport") continue;
            if (combined.Contains(s)) return true;
        }

        return false;
    }

    public sealed record Candidate(
        NetworkInterface Ni,
        IPAddress Address,
        bool HasGateway,
        bool LooksVirtual,
        int Score)
    {
        public string Display =>
            $"{Address,-15}  {Ni.Name}  [{Ni.NetworkInterfaceType}]  GW={(HasGateway ? "Y" : "N")}  Score={Score}  - {Ni.Description}";
    }
}