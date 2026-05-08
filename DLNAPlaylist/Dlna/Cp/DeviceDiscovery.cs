using System.Net;
using System.Xml.Linq;
using DLNAPlaylist.Core;
using DLNAPlaylist.Dlna.Ssdp;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Cp;

/// <summary>
/// 周期 M-SEARCH 发现 MediaRenderer，并解析 device.xml 拿到 controlURL。
/// </summary>
public sealed class DeviceDiscovery : IAsyncDisposable
{
    readonly SsdpSearcher _searcher;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    readonly LogSink _log;
    readonly Action<IReadOnlyList<RemoteDevice>> _onUpdate;
    readonly CancellationTokenSource _cts = new();
    Task? _loopTask;

    readonly Dictionary<string, RemoteDevice> _byUdn = new();
    readonly object _lock = new();

    public DeviceDiscovery(SsdpSearcher searcher, Action<IReadOnlyList<RemoteDevice>> onUpdate, LogSink log)
    {
        _searcher = searcher;
        _onUpdate = onUpdate;
        _log = log;
    }

    public void Start()
    {
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    async Task LoopAsync(CancellationToken ct)
    {
        // 首次立即搜
        try { await ScanOnceAsync(ct).ConfigureAwait(false); } catch { }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                await ScanOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Warn("CP", $"discovery iteration error: {ex.Message}");
            }
        }
    }

    public async Task ScanOnceAsync(CancellationToken ct)
    {
        var hits = await _searcher.SearchAsync(
            DlnaConstants.DeviceTypeMediaRenderer,
            TimeSpan.FromSeconds(4),
            mx: 3,
            ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var updated = false;
        foreach (var hit in hits)
        {
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
                _log.Warn("CP", $"resolve {hit.Location}: {ex.Message}");
            }
        }

        // 清理 5 分钟没出现的
        lock (_lock)
        {
            var stale = _byUdn.Where(kv => (now - kv.Value.LastSeen) > TimeSpan.FromMinutes(5))
                              .Select(kv => kv.Key).ToList();
            foreach (var k in stale) { _byUdn.Remove(k); updated = true; }
        }

        if (updated)
        {
            IReadOnlyList<RemoteDevice> snapshot;
            lock (_lock) snapshot = _byUdn.Values.ToArray();
            _onUpdate(snapshot);
        }
    }

    async Task<RemoteDevice?> ResolveAsync(SsdpSearchResult hit, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(hit.Location, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);

        XNamespace ns = "urn:schemas-upnp-org:device-1-0";
        var device = doc.Descendants(ns + "device").FirstOrDefault()
                  ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
        if (device is null) return null;

        string El(string name) => device.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
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

        string SubEl(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

        var ctrl = SubEl(avTransport, "controlURL");
        var evt = SubEl(avTransport, "eventSubURL");

        var baseUri = hit.Location;
        Uri controlUri = new(baseUri, ctrl);
        Uri? eventUri = string.IsNullOrWhiteSpace(evt) ? null : new Uri(baseUri, evt);

        return new RemoteDevice(udn, friendly, manu, model, hit.Location, controlUri, eventUri, DateTimeOffset.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_loopTask is not null) await _loopTask.ConfigureAwait(false); } catch { }
        _http.Dispose();
        _cts.Dispose();
    }
}
