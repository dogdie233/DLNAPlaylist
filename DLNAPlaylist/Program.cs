using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

using DLNAPlaylist.Core;
using DLNAPlaylist.Dlna.Cp;
using DLNAPlaylist.Dlna.Http;
using DLNAPlaylist.Dlna.Services;
using DLNAPlaylist.Dlna.Ssdp;
using DLNAPlaylist.Media;
using DLNAPlaylist.Tui;
using DLNAPlaylist.Util;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------
// 1. CLI 解析
// ---------------------------------------------------------------------------
IPAddress? userBind = null;
int? userPort = null;
var listNics = false;
var autoNic = false;

for (var i = 0; i < args.Length; i++)
{
    var a = args[i];
    switch (a)
    {
        case "--list-nics":
            listNics = true;
            break;
        case "--auto-nic":
            autoNic = true;
            break;
        case "--bind" when i + 1 < args.Length:
            if (!IPAddress.TryParse(args[++i], out userBind))
            {
                Console.Error.WriteLine($"--bind 参数不是有效的 IPv4 地址：{args[i]}");
                return 1;
            }
            break;
        case "--port" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out var p) || p <= 0 || p > 65535)
            {
                Console.Error.WriteLine($"--port 不是有效端口：{args[i]}");
                return 1;
            }
            userPort = p;
            break;
        case "-h" or "--help":
            PrintHelp();
            return 0;
        default:
            Console.Error.WriteLine($"未知参数：{a}");
            PrintHelp();
            return 1;
    }
}

if (listNics)
{
    Console.WriteLine("候选网卡（按评分排序，第一行是默认会被选中的）：");
    Console.WriteLine();
    foreach (var c in NetworkInterfaces.EnumerateCandidates())
    {
        var marker = c.LooksVirtual ? "[VIRTUAL]" : "         ";
        Console.WriteLine($"  {marker}  {c.Display}");
    }
    Console.WriteLine();
    Console.WriteLine("用 `--bind <IP>` 强制绑定其中一张。");
    return 0;
}

// ---------------------------------------------------------------------------
// 2. 选定网卡 + 端口（启动期决定，后续注入 AppOptions）
// ---------------------------------------------------------------------------
IPAddress bindAddr;
if (userBind is not null)
{
    bindAddr = userBind;
}
else
{
    var candidates = NetworkInterfaces.EnumerateCandidates();
    if (candidates.Count == 0)
    {
        Console.Error.WriteLine("找不到可用的 IPv4 网卡。试试 --list-nics 看看候选。");
        return 1;
    }

    NetworkInterfaces.Candidate chosen;
    if (candidates.Count == 1 || autoNic || Console.IsInputRedirected)
    {
        chosen = candidates[0];
    }
    else
    {
        var picked = NicSelector.Select(false);
        if (picked is null)
        {
            Console.Error.WriteLine("未选定网卡。");
            return 1;
        }
        chosen = picked;
    }
    bindAddr = chosen.Address;
}

int httpPort;
if (userPort.HasValue)
{
    httpPort = userPort.Value;
}
else
{
    httpPort = 49200;
    for (; httpPort < 50000; httpPort++)
    {
        try
        {
            using var t = new TcpListener(bindAddr, httpPort);
            t.Start();
            t.Stop();
            break;
        }
        catch
        {
            // ignored
        }
    }
}

var appOptions = new AppOptions
{
    BindAddress = bindAddr,
    HttpPort = httpPort,
    Udn = Guid.NewGuid().ToString("D"),
    FriendlyName = $"DLNAPlaylist@{Environment.MachineName}",
};

// ---------------------------------------------------------------------------
// 3. 组装 host —— 全部走 DI
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateSlimBuilder(args);

// Kestrel 监听我们选定的 IP
builder.WebHost.ConfigureKestrel(opts => opts.Listen(appOptions.BindAddress, appOptions.HttpPort));

// --- 日志：把 ASP.NET Core / Kestrel 全部桥到我们的 LogSink ---
// LogSink 本身要先 new 出来（它依赖 EventBus，这俩也在容器里注册一下保持一致性）
var bus = new EventBus<CoordinatorEvent>();
var logSink = new LogSink(bus);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new LogSinkLoggerProvider(logSink));
builder.Logging.SetMinimumLevel(LogLevel.Information);
// Kestrel 的请求日志比较吵，单独压低
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

// --- 容器登记 ---
var services = builder.Services;
services.AddSingleton(appOptions);
services.AddSingleton(bus);
services.AddSingleton(logSink);
services.AddSingleton<AllowList>();
services.AddSingleton<CurrentTargetHolder>();
services.AddSingleton<MediaCache>();
services.AddSingleton<IMediaUrlResolver, LocalCachingMediaUrlResolver>();

// 命令 channel —— 协调器是消费者，其他人是生产者
var commandChannel = Channel.CreateUnbounded<CoordinatorCommand>();
services.AddSingleton(commandChannel);
services.AddSingleton(commandChannel.Writer);

// SSDP / CP
services.AddSingleton<SsdpSearcher>();
services.AddSingleton<SsdpAdvertiser>();
services.AddHostedService(sp => sp.GetRequiredService<SsdpAdvertiser>());

services.AddSingleton<AvTransportClient>();
services.AddSingleton<DeviceDiscovery>();
services.AddHostedService(sp => sp.GetRequiredService<DeviceDiscovery>());
services.AddSingleton<RemoteStatePoller>();
services.AddHostedService(sp => sp.GetRequiredService<RemoteStatePoller>());

// MR 端
services.AddSingleton<DeviceHttpEndpoints>();
services.AddSingleton<AvTransportService>();
services.AddSingleton<ConnectionManagerService>();
services.AddSingleton<RenderingControlService>();

// 协调器
services.AddSingleton<PlaybackCoordinator>();
services.AddHostedService(sp => sp.GetRequiredService<PlaybackCoordinator>());

// TUI
services.AddSingleton<TuiApp>();
services.AddHostedService(sp => sp.GetRequiredService<TuiApp>());

// ---------------------------------------------------------------------------
// 4. 构建 + 注册 endpoints + 跑
// ---------------------------------------------------------------------------
var app = builder.Build();

// 把 DLNA endpoints 挂上 Kestrel 路由
var endpoints = app.Services.GetRequiredService<DeviceHttpEndpoints>();
endpoints.MapTo(app);

// 让 SOAP service 注册各自的 handler 到 endpoints 上
app.Services.GetRequiredService<AvTransportService>().RegisterTo(endpoints);
app.Services.GetRequiredService<ConnectionManagerService>().RegisterTo(endpoints);
app.Services.GetRequiredService<RenderingControlService>().RegisterTo(endpoints);

logSink.Info("Boot", $"UDN=uuid:{appOptions.Udn}");
logSink.Info("Boot", $"Bind: {appOptions.BindAddress}:{appOptions.HttpPort}");

try
{
    await app.RunAsync();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"启动失败：{ex.Message}");
    Console.Error.WriteLine("常见原因：端口被占用 / 选错网卡。--list-nics 看候选，--bind/--port 重新指定。");
    return 1;
}

return 0;

static void PrintHelp()
{
    Console.WriteLine("DLNAPlaylist - DLNA 播放队列 TUI");
    Console.WriteLine();
    Console.WriteLine("用法：DLNAPlaylist [选项]");
    Console.WriteLine();
    Console.WriteLine("选项：");
    Console.WriteLine("  --list-nics       列出所有候选网卡并退出");
    Console.WriteLine("  --bind <IP>       直接绑定到指定 IPv4 地址（跳过启动选择菜单）");
    Console.WriteLine("  --auto-nic        多网卡时不弹菜单，直接用评分最高的（脚本/无人值守场景）");
    Console.WriteLine("  --port <端口>     强制使用指定 HTTP 端口（默认 49200+ 自动选可用）");
    Console.WriteLine("  -h, --help        显示帮助");
    Console.WriteLine();
    Console.WriteLine("默认行为：检测到多张网卡时启动会弹 TUI 选择菜单。");
}
