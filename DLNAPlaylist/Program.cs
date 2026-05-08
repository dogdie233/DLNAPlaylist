using System.Threading.Channels;
using DLNAPlaylist.Core;
using DLNAPlaylist.Dlna.Cp;
using DLNAPlaylist.Dlna.Http;
using DLNAPlaylist.Dlna.Services;
using DLNAPlaylist.Dlna.Ssdp;
using DLNAPlaylist.Media;
using DLNAPlaylist.Tui;
using DLNAPlaylist.Util;

// --- 基础设施 ---
var eventBus = new EventBus<CoordinatorEvent>();
var log = new LogSink(eventBus);
var allowList = new AllowList();

// UDN 会话常量（不持久化）
var udn = Guid.NewGuid().ToString("D");
var friendlyName = $"DLNAPlaylist@{Environment.MachineName}";

// 网卡与端口
var bindAddr = NetworkInterfaces.PickPrimaryIPv4();
// 动态选一个 HTTP 端口：找个可用的 49152+
int httpPort = 49200;
for (; httpPort < 50000; httpPort++)
{
    try
    {
        using var t = new System.Net.Sockets.TcpListener(bindAddr, httpPort);
        t.Start();
        t.Stop();
        break;
    }
    catch { }
}

log.Info("Boot", $"UDN=uuid:{udn}");
log.Info("Boot", $"Bind IP: {bindAddr}, HTTP: {httpPort}");

// --- 命令 channel ---
var commandChannel = Channel.CreateUnbounded<CoordinatorCommand>();

// --- CP 侧 ---
var cpClient = new AvTransportClient(log);
var resolver = new PassthroughMediaUrlResolver();
var coordinator = new PlaybackCoordinator(
    commandChannel, eventBus, log, cpClient, resolver, allowList);

// --- MR 服务 ---
var httpServer = new DeviceHttpServer(bindAddr, httpPort, udn, friendlyName, log);
var avtService = new AvTransportService(commandChannel.Writer, log);
var cmService  = new ConnectionManagerService();
var rcService  = new RenderingControlService();
avtService.RegisterTo(httpServer);
cmService.RegisterTo(httpServer);
rcService.RegisterTo(httpServer);
httpServer.Start();

// --- SSDP 广告 ---
var advertiser = new SsdpAdvertiser(bindAddr, httpPort, udn, log);
advertiser.Start();

// --- CP 发现 ---
var searcher = new SsdpSearcher(bindAddr, log);
var discovery = new DeviceDiscovery(searcher,
    devices => commandChannel.Writer.TryWrite(new CoordinatorCommand.DevicesDiscovered(devices)),
    log);
discovery.Start();

// --- CP 状态轮询 ---
var poller = new RemoteStatePoller(cpClient, () => coordinator.CurrentTarget, commandChannel.Writer, log);
poller.Start();

// --- 协调器 ---
coordinator.Start();

// --- TUI ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var tui = new TuiApp(eventBus, log, commandChannel.Writer);

try
{
    await tui.RunAsync(cts.Token);
}
catch (OperationCanceledException) { }
finally
{
    await advertiser.DisposeAsync();
    await discovery.DisposeAsync();
    await poller.DisposeAsync();
    await httpServer.DisposeAsync();
    await coordinator.DisposeAsync();
    cpClient.Dispose();
}
