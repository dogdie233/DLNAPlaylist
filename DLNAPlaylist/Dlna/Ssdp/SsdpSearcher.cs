using System.Net;
using System.Net.Sockets;
using System.Text;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Ssdp;

public sealed record SsdpSearchResult(string Usn, string St, Uri Location, IPEndPoint From);

/// <summary>
/// 主动 M-SEARCH 发现 MediaRenderer。
/// 用一次性 socket 发出，再在有限窗口内收响应。
/// </summary>
public sealed class SsdpSearcher
{
    readonly IPAddress _bindAddr;
    readonly LogSink _log;

    public SsdpSearcher(IPAddress bindAddr, LogSink log)
    {
        _bindAddr = bindAddr;
        _log = log;
    }

    public async Task<List<SsdpSearchResult>> SearchAsync(string st, TimeSpan timeout, int mx = 3, CancellationToken ct = default)
    {
        var results = new List<SsdpSearchResult>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(_bindAddr, 0));
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
            catch (OperationCanceledException) { break; }
            catch (Exception) { continue; }

            var msg = Encoding.UTF8.GetString(res.Buffer);
            if (!msg.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)) continue;

            var location = ParseHeader(msg, "LOCATION");
            var usn = ParseHeader(msg, "USN");
            var stHead = ParseHeader(msg, "ST");
            if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(usn) || string.IsNullOrWhiteSpace(stHead))
                continue;
            if (!Uri.TryCreate(location, UriKind.Absolute, out var loc)) continue;

            // 去重
            if (results.Any(r => r.Usn == usn)) continue;
            results.Add(new SsdpSearchResult(usn!, stHead!, loc, res.RemoteEndPoint));
        }

        return results;
    }

    static string? ParseHeader(string httpMsg, string name)
    {
        using var reader = new StringReader(httpMsg);
        string? line;
        while ((line = reader.ReadLine()) is not null)
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
