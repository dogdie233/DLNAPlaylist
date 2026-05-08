using DLNAPlaylist.Dlna.Http;

namespace DLNAPlaylist.Dlna.Services;

public sealed class ConnectionManagerService
{
    public void RegisterTo(DeviceHttpServer server)
    {
        server.Register("ConnectionManager", "GetProtocolInfo", _ =>
        {
            // 宽松清单，声明能吃常见编码；实际不转码，让电视自己去播原始 URL。
            IReadOnlyList<(string, string)> outArgs = new (string, string)[]
            {
                ("Source", ""),
                ("Sink",
                    "http-get:*:video/*:*,http-get:*:audio/*:*,"
                    + "http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,"
                    + "http-get:*:audio/mpeg:*,http-get:*:audio/mp4:*,http-get:*:audio/flac:*"),
            };
            return Task.FromResult(outArgs);
        });

        server.Register("ConnectionManager", "GetCurrentConnectionIDs", _ =>
        {
            IReadOnlyList<(string, string)> outArgs = new (string, string)[]
            {
                ("ConnectionIDs", "0"),
            };
            return Task.FromResult(outArgs);
        });

        server.Register("ConnectionManager", "GetCurrentConnectionInfo", _ =>
        {
            IReadOnlyList<(string, string)> outArgs = new (string, string)[]
            {
                ("RcsID", "0"),
                ("AVTransportID", "0"),
                ("ProtocolInfo", ""),
                ("PeerConnectionManager", ""),
                ("PeerConnectionID", "-1"),
                ("Direction", "Input"),
                ("Status", "OK"),
            };
            return Task.FromResult(outArgs);
        });
    }
}
