using DLNAPlaylist.Dlna.Http;

namespace DLNAPlaylist.Dlna.Services;

/// <summary>
/// RenderingControl 最小实现：返回固定音量，避免 CP 因缺服务不投。
/// </summary>
public sealed class RenderingControlService
{
    public void RegisterTo(DeviceHttpServer server)
    {
        server.Register("RenderingControl", "GetVolume", _ =>
        {
            IReadOnlyList<(string, string)> outArgs = new (string, string)[]
            {
                ("CurrentVolume", "50"),
            };
            return Task.FromResult(outArgs);
        });

        server.Register("RenderingControl", "SetVolume", _ =>
        {
            IReadOnlyList<(string, string)> o = Array.Empty<(string, string)>();
            return Task.FromResult(o);
        });

        server.Register("RenderingControl", "GetMute", _ =>
        {
            IReadOnlyList<(string, string)> outArgs = new (string, string)[]
            {
                ("CurrentMute", "0"),
            };
            return Task.FromResult(outArgs);
        });
    }
}
