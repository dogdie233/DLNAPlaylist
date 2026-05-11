using System.Net;
using System.Net.Sockets;
using System.Text;

using DLNAPlaylist.Core;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Ssdp;

/// <summary>
///     周期性广播本 MediaRenderer（ssdp:alive）并响应 M-SEARCH。
///     需要发送这几条 NT：
///     - upnp:rootdevice
///     - uuid:{DeviceUdn}
///     - urn:schemas-upnp-org:device:MediaRenderer:1
///     - urn:schemas-upnp-org:service:AVTransport:1
///     - urn:schemas-upnp-org:service:ConnectionManager:1
///     - urn:schemas-upnp-org:service:RenderingControl:1
/// </summary>
public sealed class SsdpAdvertiser(AppOptions opts, LogSink log) : IHostedService, IAsyncDisposable
{
    private readonly IPAddress _bindAddr = opts.BindAddress;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _httpPort = opts.HttpPort;

    private readonly IPEndPoint _multicastEp =
        new(IPAddress.Parse(DlnaConstants.SsdpMulticastAddr), DlnaConstants.SsdpPort);

    private readonly string _udn = opts.Udn;
    private Task? _heartbeatTask;
    private Task? _responderTask;

    private UdpClient? _socket;

    public string LocationUrl => $"http://{_bindAddr}:{_httpPort}/device.xml";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await SendByeByeAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        await _cts.CancelAsync();
        try
        {
            if (_responderTask is not null)
                await _responderTask.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_heartbeatTask is not null)
                await _heartbeatTask.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _socket?.Dispose();
        _cts.Dispose();
    }

    public void Start()
    {
        _socket = new UdpClient(AddressFamily.InterNetwork);
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            // 有些平台支持 ReuseAddress 即可；Windows 上这样够用。
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, DlnaConstants.SsdpPort));
        }
        catch (SocketException ex)
        {
            log.Warn("SSDP", $"绑定 1900 端口失败（可能被占用）：{ex.Message}；M-SEARCH 将收不到。");
        }

        try
        {
            _socket.JoinMulticastGroup(IPAddress.Parse(DlnaConstants.SsdpMulticastAddr), _bindAddr);
        }
        catch (Exception ex)
        {
            log.Warn("SSDP", $"加入多播组失败：{ex.Message}");
        }

        // 关键：明确指定多播出站接口，否则在多网卡下 Windows 按路由表选
        // 很可能走到 VPN 网卡上去（OpenVPN 会注入默认路由）。
        try
        {
            _socket.Client.SetSocketOption(
                SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                _bindAddr.GetAddressBytes());
        }
        catch (Exception ex)
        {
            log.Warn("SSDP", $"设置多播出站接口失败：{ex.Message}");
        }

        _socket.MulticastLoopback = false;

        _responderTask = Task.Run(() => ResponderLoopAsync(_cts.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

        log.Info("SSDP", $"Advertiser started on {_bindAddr}; Location={LocationUrl}; UDN=uuid:{_udn}");

        // 首发 alive
        _ = SendAliveAsync();
    }

    private async Task ResponderLoopAsync(CancellationToken ct)
    {
        var sock = _socket!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await sock.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warn("SSDP", $"接收失败：{ex.Message}");
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            var msg = Encoding.UTF8.GetString(res.Buffer);
            if (!msg.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) continue;

            // 提取 ST 头
            var st = ParseHeader(msg, "ST") ?? "";
            var mx = ParseHeader(msg, "MX") ?? "1";
            int.TryParse(mx, out var mxSec);
            if (mxSec < 1) mxSec = 1;
            if (mxSec > 5) mxSec = 5;

            if (!ShouldRespondTo(st)) continue;

            // 在 0..mxSec 之间随机延迟
            var delayMs = Random.Shared.Next(0, mxSec * 1000);
            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                foreach (var (usn, nt) in MatchedTargets(st))
                    await SendSearchResponseAsync(res.RemoteEndPoint, usn, nt).ConfigureAwait(false);
            }, ct);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        // 标准建议 cache-control max-age=1800，隔 ~15 分钟重发一次；这里 10 分钟更保险。
        var interval = TimeSpan.FromMinutes(10);
        while (!ct.IsCancellationRequested)
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                await SendAliveAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warn("SSDP", $"heartbeat error: {ex.Message}");
            }
    }

    public async Task SendAliveAsync()
    {
        foreach (var (usn, nt) in AllTargets())
        {
            var body = BuildNotify(usn, nt, true);
            await SendToMulticastAsync(body).ConfigureAwait(false);
        }
    }

    public async Task SendByeByeAsync()
    {
        foreach (var (usn, nt) in AllTargets())
        {
            var body = BuildNotify(usn, nt, false);
            await SendToMulticastAsync(body).ConfigureAwait(false);
        }
    }

    private async Task SendToMulticastAsync(string body)
    {
        if (_socket is null) return;
        var bytes = Encoding.UTF8.GetBytes(body);
        try
        {
            await _socket.SendAsync(bytes, bytes.Length, _multicastEp).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("SSDP", $"multicast send failed: {ex.Message}");
        }
    }

    private async Task SendSearchResponseAsync(IPEndPoint to, string usn, string st)
    {
        if (_socket is null) return;
        var body =
            "HTTP/1.1 200 OK\r\n" +
            "CACHE-CONTROL: max-age=1800\r\n" +
            $"DATE: {DateTime.UtcNow:R}\r\n" +
            "EXT:\r\n" +
            $"LOCATION: {LocationUrl}\r\n" +
            $"SERVER: {DlnaConstants.ServerHeader}\r\n" +
            $"ST: {st}\r\n" +
            $"USN: {usn}\r\n" +
            "\r\n";
        var bytes = Encoding.UTF8.GetBytes(body);
        try
        {
            await _socket.SendAsync(bytes, bytes.Length, to).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("SSDP", $"M-SEARCH reply failed: {ex.Message}");
        }
    }

    private string BuildNotify(string usn, string nt, bool alive)
    {
        return "NOTIFY * HTTP/1.1\r\n" +
               $"HOST: {DlnaConstants.SsdpMulticastAddr}:{DlnaConstants.SsdpPort}\r\n" +
               "CACHE-CONTROL: max-age=1800\r\n" +
               $"LOCATION: {LocationUrl}\r\n" +
               $"NT: {nt}\r\n" +
               $"NTS: {(alive ? "ssdp:alive" : "ssdp:byebye")}\r\n" +
               $"SERVER: {DlnaConstants.ServerHeader}\r\n" +
               $"USN: {usn}\r\n" +
               "\r\n";
    }

    private IEnumerable<(string usn, string nt)> AllTargets()
    {
        yield return ($"uuid:{_udn}::upnp:rootdevice", "upnp:rootdevice");
        yield return ($"uuid:{_udn}", $"uuid:{_udn}");
        yield return ($"uuid:{_udn}::{DlnaConstants.DeviceTypeMediaRenderer}", DlnaConstants.DeviceTypeMediaRenderer);
        yield return ($"uuid:{_udn}::{DlnaConstants.ServiceTypeAvTransport}", DlnaConstants.ServiceTypeAvTransport);
        yield return ($"uuid:{_udn}::{DlnaConstants.ServiceTypeConnectionManager}",
            DlnaConstants.ServiceTypeConnectionManager);
        yield return ($"uuid:{_udn}::{DlnaConstants.ServiceTypeRenderingControl}",
            DlnaConstants.ServiceTypeRenderingControl);
    }

    private IEnumerable<(string usn, string nt)> MatchedTargets(string st)
    {
        if (st == "ssdp:all")
        {
            foreach (var x in AllTargets()) yield return x;
            yield break;
        }

        foreach (var (usn, nt) in AllTargets())
            if (string.Equals(nt, st, StringComparison.OrdinalIgnoreCase))
                yield return (usn, nt);
    }

    private bool ShouldRespondTo(string st)
    {
        if (st == "ssdp:all") return true;
        return AllTargets().Any(t => string.Equals(t.nt, st, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ParseHeader(string httpMsg, string name)
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