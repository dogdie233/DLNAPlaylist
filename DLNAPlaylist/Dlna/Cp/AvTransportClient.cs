using DLNAPlaylist.Core;
using DLNAPlaylist.Dlna.Soap;
using DLNAPlaylist.Util;

namespace DLNAPlaylist.Dlna.Cp;

/// <summary>
/// 对 AVTransport 的高层封装：SetURI/Play/Pause/Stop/Seek/Get*。
/// </summary>
public sealed class AvTransportClient : IDisposable
{
    readonly SoapClient _soap;
    readonly LogSink _log;

    public AvTransportClient(LogSink log)
    {
        _log = log;
        _soap = new SoapClient(log);
    }

    public async Task SetUriAsync(RemoteDevice device, string uri, string didlLite, CancellationToken ct)
    {
        await _soap.InvokeAsync(device.AvTransportControlUrl,
            DlnaConstants.ServiceTypeAvTransport, "SetAVTransportURI",
            new (string, string)[]
            {
                ("InstanceID", "0"),
                ("CurrentURI", uri),
                ("CurrentURIMetaData", didlLite),
            }, ct).ConfigureAwait(false);
    }

    public async Task PlayAsync(RemoteDevice device, CancellationToken ct)
    {
        await _soap.InvokeAsync(device.AvTransportControlUrl,
            DlnaConstants.ServiceTypeAvTransport, "Play",
            new (string, string)[]
            {
                ("InstanceID", "0"),
                ("Speed", "1"),
            }, ct).ConfigureAwait(false);
    }

    public async Task PauseAsync(RemoteDevice device, CancellationToken ct)
    {
        await _soap.InvokeAsync(device.AvTransportControlUrl,
            DlnaConstants.ServiceTypeAvTransport, "Pause",
            new (string, string)[] { ("InstanceID", "0") }, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(RemoteDevice device, CancellationToken ct)
    {
        await _soap.InvokeAsync(device.AvTransportControlUrl,
            DlnaConstants.ServiceTypeAvTransport, "Stop",
            new (string, string)[] { ("InstanceID", "0") }, ct).ConfigureAwait(false);
    }

    public async Task SeekAsync(RemoteDevice device, TimeSpan position, CancellationToken ct)
    {
        await _soap.InvokeAsync(device.AvTransportControlUrl,
            DlnaConstants.ServiceTypeAvTransport, "Seek",
            new (string, string)[]
            {
                ("InstanceID", "0"),
                ("Unit", "REL_TIME"),
                ("Target", DlnaTime.Format(position)),
            }, ct).ConfigureAwait(false);
    }

    public async Task<(string State, string Status)> GetTransportInfoAsync(RemoteDevice device, CancellationToken ct)
    {
        var resp = await _soap.InvokeAsync(device.AvTransportControlUrl,
            DlnaConstants.ServiceTypeAvTransport, "GetTransportInfo",
            new (string, string)[] { ("InstanceID", "0") }, ct).ConfigureAwait(false);
        string El(string name) =>
            resp.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        return (El("CurrentTransportState"), El("CurrentTransportStatus"));
    }

    public async Task<(TimeSpan? Position, TimeSpan? Duration)> GetPositionInfoAsync(RemoteDevice device, CancellationToken ct)
    {
        var resp = await _soap.InvokeAsync(device.AvTransportControlUrl,
            DlnaConstants.ServiceTypeAvTransport, "GetPositionInfo",
            new (string, string)[] { ("InstanceID", "0") }, ct).ConfigureAwait(false);
        string El(string name) =>
            resp.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        var pos = DlnaTime.TryParse(El("RelTime"));
        var dur = DlnaTime.TryParse(El("TrackDuration"));
        return (pos, dur);
    }

    public void Dispose() => _soap.Dispose();
}
