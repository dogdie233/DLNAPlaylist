using System.Threading.Channels;
using System.Xml.Linq;

using DLNAPlaylist.Core;
using DLNAPlaylist.Dlna.Ssdp;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Cp;

/// <summary>
///     周期 M-SEARCH 发现 MediaRenderer，并解析 device.xml 拿到 controlURL。
/// </summary>
public sealed class DeviceDiscovery(SsdpSearcher searcher, ChannelWriter<CoordinatorCommand> commands, LogSink log)
    : IHostedService, IAsyncDisposable
{
    private readonly Dictionary<string, RemoteDevice> _byUdn = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly Lock _lock = new();
    private Task? _loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            if (_loopTask is not null) await _loopTask.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _http.Dispose();
        _cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // 首次立即搜
        try
        {
            await ScanOnceAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                await ScanOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warn("CP", $"discovery iteration error: {ex.Message}");
            }
        }
    }

    public async Task ScanOnceAsync(CancellationToken ct)
    {
        var hits = await searcher.SearchAsync(
            DlnaConstants.DeviceTypeMediaRenderer,
            TimeSpan.FromSeconds(4),
            3,
            ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var updated = false;
        foreach (var hit in hits)
            try
            {
                var dev = await ResolveAsync(hit, ct).ConfigureAwait(false);
                if (dev is null) continue;
                lock (_lock)
                {
                    _byUdn[dev.Udn] = dev;
                }

                updated = true;
            }
            catch (Exception ex)
            {
                log.Warn("CP", $"resolve {hit.Location}: {ex.Message}");
            }

        // 清理 5 分钟没出现的
        lock (_lock)
        {
            var stale = _byUdn.Where(kv => now - kv.Value.LastSeen > TimeSpan.FromMinutes(5))
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale)
            {
                _byUdn.Remove(k);
                updated = true;
            }
        }

        if (updated)
        {
            IReadOnlyList<RemoteDevice> snapshot;
            lock (_lock)
            {
                snapshot = _byUdn.Values.ToArray();
            }

            commands.TryWrite(new CoordinatorCommand.DevicesDiscovered(snapshot));
        }
    }

    private async Task<RemoteDevice?> ResolveAsync(SsdpSearchResult hit, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(hit.Location, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);

        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        var device = doc.Descendants(ns + "device").FirstOrDefault()
                     ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
        if (device is null) return null;

        string El(string name)
        {
            return device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        }

        var udn = El("UDN");
        if (string.IsNullOrWhiteSpace(udn)) return null;
        var friendly = El("friendlyName");
        var manu = El("manufacturer");
        var model = El("modelName");

        var avTransport = device.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "service" &&
            (e.Elements().FirstOrDefault(c => c.Name.LocalName == "serviceType")?.Value ?? "")
            .Contains("AVTransport:1", StringComparison.OrdinalIgnoreCase));
        if (avTransport is null) return null;

        string SubEl(XElement parent, string name)
        {
            return parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        }

        var ctrl = SubEl(avTransport, "controlURL");
        var evt = SubEl(avTransport, "eventSubURL");

        var baseUri = hit.Location;
        Uri controlUri = new(baseUri, ctrl);
        var eventUri = string.IsNullOrWhiteSpace(evt) ? null : new Uri(baseUri, evt);

        return new RemoteDevice(udn, friendly, manu, model, hit.Location, controlUri, eventUri, DateTimeOffset.UtcNow);
    }
}