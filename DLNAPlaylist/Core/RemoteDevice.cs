namespace DLNAPlaylist.Core;

/// <summary>
///     被发现的远端 MR (电视/盒子) 的描述。
/// </summary>
public sealed record RemoteDevice(
    string Udn,
    string FriendlyName,
    string Manufacturer,
    string ModelName,
    Uri Location,
    Uri AvTransportControlUrl,
    Uri? AvTransportEventUrl,
    DateTimeOffset LastSeen)
{
    public string Display => string.IsNullOrWhiteSpace(FriendlyName) ? Udn : FriendlyName;
}