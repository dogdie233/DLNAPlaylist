using System.Net;
using System.Net.Sockets;
using System.Text;

using DLNAPlaylist.Core;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Ssdp;

public sealed record SsdpSearchResult(string Usn, string St, Uri Location, IPEndPoint From);

/// <summary>
///     主动 M-SEARCH 发现 MediaRenderer。
///     用一次性 socket 发出，再在有限窗口内收响应。
/// </summary>
public sealed class SsdpSearcher
{
    private readonly IPAddress _bindAddr;
    private readonly LogSink _log;

    public SsdpSearcher(AppOptions opts, LogSink log)
    {
        _bindAddr = opts.BindAddress;
        _log = log;
    }

    public async Task<List<SsdpSearchResult>> SearchAsync(string st, TimeSpan timeout, int mx = 3,
        CancellationToken ct = default)
    {
        var results = new List<SsdpSearchResult>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(_bindAddr, 0));
        // 关键：明确指定多播出站接口（防多网卡时被 VPN 网卡抢路由）
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                _bindAddr.GetAddressBytes());
        }
        catch
        {
            /* 非 Windows 上某些情况返回 EINVAL，不致命 */
        }

        udp.MulticastLoopback = false;

        var mcast = new IPEndPoint(IPAddress.Parse(DlnaConstants.SsdpMulticastAddr), DlnaConstants.SsdpPort);
        var req =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {DlnaConstants.SsdpMulticastAddr}:{DlnaConstants.SsdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            $"MX: {mx}\r\n" +
            $"ST: {st}\r\n" +
            $"USER-AGENT: {DlnaConstants.SsdpUserAgent}\r\n" +
            "\r\n";
        var bytes = Encoding.UTF8.GetBytes(req);

        try
        {
            // 发两次，抵抗丢包
            await udp.SendAsync(bytes, bytes.Length, mcast).ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false);
            await udp.SendAsync(bytes, bytes.Length, mcast).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("SSDP", $"M-SEARCH 发送失败：{ex.Message}");
            return results;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                continue;
            }

            var msg = Encoding.UTF8.GetString(res.Buffer);
            if (!msg.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)) continue;

            var location = ParseHeader(msg, "LOCATION");
            var usn = ParseHeader(msg, "USN");
            var stHead = ParseHeader(msg, "ST");
            if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(usn) ||
                string.IsNullOrWhiteSpace(stHead))
                continue;
            if (!Uri.TryCreate(location, UriKind.Absolute, out var loc)) continue;

            // 去重
            if (results.Any(r => r.Usn == usn)) continue;
            results.Add(new SsdpSearchResult(usn, stHead, loc, res.RemoteEndPoint));
        }

        return results;
    }

    private static string? ParseHeader(string httpMsg, string name)
    {
        using var reader = new StringReader(httpMsg);
        while (reader.ReadLine() is { } line)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var h = line[..idx].Trim();
            if (string.Equals(h, name, StringComparison.OrdinalIgnoreCase))
                return line[(idx + 1)..].Trim();
        }

        return null;
    }
}