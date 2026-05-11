using DLNAPlaylist.Dlna.Http;

namespace DLNAPlaylist.Dlna.Services;

/// <summary>
///     RenderingControl 最小实现：返回固定音量，避免 CP 因缺服务不投。
/// </summary>
public sealed class RenderingControlService
{
    public void RegisterTo(DeviceHttpEndpoints server)
    {
        server.Register("RenderingControl", "GetVolume", _ =>
        {
            IReadOnlyList<(string, string)> outArgs =
            [
                ("CurrentVolume", "50")
            ];
            return Task.FromResult(outArgs);
        });

        server.Register("RenderingControl", "SetVolume", _ =>
        {
            IReadOnlyList<(string, string)> o = [];
            return Task.FromResult(o);
        });

        server.Register("RenderingControl", "GetMute", _ =>
        {
            IReadOnlyList<(string, string)> outArgs =
            [
                ("CurrentMute", "0")
            ];
            return Task.FromResult(outArgs);
        });
    }
}