using System.Net;

namespace DLNAPlaylist.Dlna.Http;

/// <summary>
///     设备 / 服务描述 XML 模板。保持最小可用，符合 UPnP MR 规范的必备字段。
/// </summary>
public static class DeviceXml
{
  /// <summary>
  ///     AVTransport 最小 SCPD。只声明我们实际会响应的 action，其余返回 501。
  /// </summary>
  public const string AvTransportScpd = """
                                        <?xml version="1.0" encoding="utf-8"?>
                                        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
                                          <specVersion><major>1</major><minor>0</minor></specVersion>
                                          <actionList>
                                            <action><name>SetAVTransportURI</name><argumentList>
                                              <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                              <argument><name>CurrentURI</name><direction>in</direction><relatedStateVariable>AVTransportURI</relatedStateVariable></argument>
                                              <argument><name>CurrentURIMetaData</name><direction>in</direction><relatedStateVariable>AVTransportURIMetaData</relatedStateVariable></argument>
                                            </argumentList></action>
                                            <action><name>Play</name><argumentList>
                                              <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                              <argument><name>Speed</name><direction>in</direction><relatedStateVariable>TransportPlaySpeed</relatedStateVariable></argument>
                                            </argumentList></action>
                                            <action><name>Pause</name><argumentList>
                                              <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                            </argumentList></action>
                                            <action><name>Stop</name><argumentList>
                                              <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                            </argumentList></action>
                                            <action><name>GetTransportInfo</name><argumentList>
                                              <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                              <argument><name>CurrentTransportState</name><direction>out</direction><relatedStateVariable>TransportState</relatedStateVariable></argument>
                                              <argument><name>CurrentTransportStatus</name><direction>out</direction><relatedStateVariable>TransportStatus</relatedStateVariable></argument>
                                              <argument><name>CurrentSpeed</name><direction>out</direction><relatedStateVariable>TransportPlaySpeed</relatedStateVariable></argument>
                                            </argumentList></action>
                                            <action><name>GetPositionInfo</name><argumentList>
                                              <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                              <argument><name>Track</name><direction>out</direction><relatedStateVariable>CurrentTrack</relatedStateVariable></argument>
                                              <argument><name>TrackDuration</name><direction>out</direction><relatedStateVariable>CurrentTrackDuration</relatedStateVariable></argument>
                                              <argument><name>TrackMetaData</name><direction>out</direction><relatedStateVariable>CurrentTrackMetaData</relatedStateVariable></argument>
                                              <argument><name>TrackURI</name><direction>out</direction><relatedStateVariable>CurrentTrackURI</relatedStateVariable></argument>
                                              <argument><name>RelTime</name><direction>out</direction><relatedStateVariable>RelativeTimePosition</relatedStateVariable></argument>
                                              <argument><name>AbsTime</name><direction>out</direction><relatedStateVariable>AbsoluteTimePosition</relatedStateVariable></argument>
                                              <argument><name>RelCount</name><direction>out</direction><relatedStateVariable>RelativeCounterPosition</relatedStateVariable></argument>
                                              <argument><name>AbsCount</name><direction>out</direction><relatedStateVariable>AbsoluteCounterPosition</relatedStateVariable></argument>
                                            </argumentList></action>
                                          </actionList>
                                          <serviceStateTable>
                                            <stateVariable sendEvents="yes"><name>LastChange</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>AVTransportURI</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>AVTransportURIMetaData</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>TransportState</name><dataType>string</dataType>
                                              <allowedValueList><allowedValue>STOPPED</allowedValue><allowedValue>PLAYING</allowedValue><allowedValue>PAUSED_PLAYBACK</allowedValue><allowedValue>TRANSITIONING</allowedValue><allowedValue>NO_MEDIA_PRESENT</allowedValue></allowedValueList></stateVariable>
                                            <stateVariable sendEvents="no"><name>TransportStatus</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>TransportPlaySpeed</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>CurrentTrack</name><dataType>ui4</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>CurrentTrackDuration</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>CurrentTrackMetaData</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>CurrentTrackURI</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>RelativeTimePosition</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>AbsoluteTimePosition</name><dataType>string</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>RelativeCounterPosition</name><dataType>i4</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>AbsoluteCounterPosition</name><dataType>i4</dataType></stateVariable>
                                            <stateVariable sendEvents="no"><name>A_ARG_TYPE_InstanceID</name><dataType>ui4</dataType></stateVariable>
                                          </serviceStateTable>
                                        </scpd>
                                        """;

    public const string ConnectionManagerScpd = """
                                                <?xml version="1.0" encoding="utf-8"?>
                                                <scpd xmlns="urn:schemas-upnp-org:service-1-0">
                                                  <specVersion><major>1</major><minor>0</minor></specVersion>
                                                  <actionList>
                                                    <action><name>GetProtocolInfo</name><argumentList>
                                                      <argument><name>Source</name><direction>out</direction><relatedStateVariable>SourceProtocolInfo</relatedStateVariable></argument>
                                                      <argument><name>Sink</name><direction>out</direction><relatedStateVariable>SinkProtocolInfo</relatedStateVariable></argument>
                                                    </argumentList></action>
                                                    <action><name>GetCurrentConnectionIDs</name><argumentList>
                                                      <argument><name>ConnectionIDs</name><direction>out</direction><relatedStateVariable>CurrentConnectionIDs</relatedStateVariable></argument>
                                                    </argumentList></action>
                                                    <action><name>GetCurrentConnectionInfo</name><argumentList>
                                                      <argument><name>ConnectionID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument>
                                                      <argument><name>RcsID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_RcsID</relatedStateVariable></argument>
                                                      <argument><name>AVTransportID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_AVTransportID</relatedStateVariable></argument>
                                                      <argument><name>ProtocolInfo</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ProtocolInfo</relatedStateVariable></argument>
                                                      <argument><name>PeerConnectionManager</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionManager</relatedStateVariable></argument>
                                                      <argument><name>PeerConnectionID</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionID</relatedStateVariable></argument>
                                                      <argument><name>Direction</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_Direction</relatedStateVariable></argument>
                                                      <argument><name>Status</name><direction>out</direction><relatedStateVariable>A_ARG_TYPE_ConnectionStatus</relatedStateVariable></argument>
                                                    </argumentList></action>
                                                  </actionList>
                                                  <serviceStateTable>
                                                    <stateVariable sendEvents="yes"><name>SourceProtocolInfo</name><dataType>string</dataType></stateVariable>
                                                    <stateVariable sendEvents="yes"><name>SinkProtocolInfo</name><dataType>string</dataType></stateVariable>
                                                    <stateVariable sendEvents="yes"><name>CurrentConnectionIDs</name><dataType>string</dataType></stateVariable>
                                                    <stateVariable sendEvents="no"><name>A_ARG_TYPE_ConnectionID</name><dataType>i4</dataType></stateVariable>
                                                    <stateVariable sendEvents="no"><name>A_ARG_TYPE_RcsID</name><dataType>i4</dataType></stateVariable>
                                                    <stateVariable sendEvents="no"><name>A_ARG_TYPE_AVTransportID</name><dataType>i4</dataType></stateVariable>
                                                    <stateVariable sendEvents="no"><name>A_ARG_TYPE_ProtocolInfo</name><dataType>string</dataType></stateVariable>
                                                    <stateVariable sendEvents="no"><name>A_ARG_TYPE_ConnectionManager</name><dataType>string</dataType></stateVariable>
                                                    <stateVariable sendEvents="no"><name>A_ARG_TYPE_Direction</name><dataType>string</dataType></stateVariable>
                                                    <stateVariable sendEvents="no"><name>A_ARG_TYPE_ConnectionStatus</name><dataType>string</dataType></stateVariable>
                                                  </serviceStateTable>
                                                </scpd>
                                                """;

    public const string RenderingControlScpd = """
                                               <?xml version="1.0" encoding="utf-8"?>
                                               <scpd xmlns="urn:schemas-upnp-org:service-1-0">
                                                 <specVersion><major>1</major><minor>0</minor></specVersion>
                                                 <actionList>
                                                   <action><name>GetVolume</name><argumentList>
                                                     <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                                     <argument><name>Channel</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Channel</relatedStateVariable></argument>
                                                     <argument><name>CurrentVolume</name><direction>out</direction><relatedStateVariable>Volume</relatedStateVariable></argument>
                                                   </argumentList></action>
                                                   <action><name>SetVolume</name><argumentList>
                                                     <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                                     <argument><name>Channel</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Channel</relatedStateVariable></argument>
                                                     <argument><name>DesiredVolume</name><direction>in</direction><relatedStateVariable>Volume</relatedStateVariable></argument>
                                                   </argumentList></action>
                                                   <action><name>GetMute</name><argumentList>
                                                     <argument><name>InstanceID</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_InstanceID</relatedStateVariable></argument>
                                                     <argument><name>Channel</name><direction>in</direction><relatedStateVariable>A_ARG_TYPE_Channel</relatedStateVariable></argument>
                                                     <argument><name>CurrentMute</name><direction>out</direction><relatedStateVariable>Mute</relatedStateVariable></argument>
                                                   </argumentList></action>
                                                 </actionList>
                                                 <serviceStateTable>
                                                   <stateVariable sendEvents="yes"><name>LastChange</name><dataType>string</dataType></stateVariable>
                                                   <stateVariable sendEvents="no"><name>Volume</name><dataType>ui2</dataType><allowedValueRange><minimum>0</minimum><maximum>100</maximum></allowedValueRange></stateVariable>
                                                   <stateVariable sendEvents="no"><name>Mute</name><dataType>boolean</dataType></stateVariable>
                                                   <stateVariable sendEvents="no"><name>A_ARG_TYPE_InstanceID</name><dataType>ui4</dataType></stateVariable>
                                                   <stateVariable sendEvents="no"><name>A_ARG_TYPE_Channel</name><dataType>string</dataType></stateVariable>
                                                 </serviceStateTable>
                                               </scpd>
                                               """;

    public static string Device(string udn, string friendlyName, int httpPort, string host)
    {
        return $"""
                <?xml version="1.0" encoding="utf-8"?>
                <root xmlns="urn:schemas-upnp-org:device-1-0">
                  <specVersion><major>1</major><minor>0</minor></specVersion>
                  <device>
                    <deviceType>{DlnaConstants.DeviceTypeMediaRenderer}</deviceType>
                    <friendlyName>{WebUtility.HtmlEncode(friendlyName)}</friendlyName>
                    <manufacturer>DLNAPlaylist</manufacturer>
                    <manufacturerURL>https://localhost/</manufacturerURL>
                    <modelDescription>DLNA Playlist TUI</modelDescription>
                    <modelName>DLNAPlaylist</modelName>
                    <modelNumber>1.0</modelNumber>
                    <UDN>uuid:{udn}</UDN>
                    <serviceList>
                      <service>
                        <serviceType>{DlnaConstants.ServiceTypeAvTransport}</serviceType>
                        <serviceId>{DlnaConstants.ServiceIdAvTransport}</serviceId>
                        <SCPDURL>/scpd/avtransport.xml</SCPDURL>
                        <controlURL>/ctrl/avtransport</controlURL>
                        <eventSubURL>/event/avtransport</eventSubURL>
                      </service>
                      <service>
                        <serviceType>{DlnaConstants.ServiceTypeConnectionManager}</serviceType>
                        <serviceId>{DlnaConstants.ServiceIdConnectionManager}</serviceId>
                        <SCPDURL>/scpd/connectionmanager.xml</SCPDURL>
                        <controlURL>/ctrl/connectionmanager</controlURL>
                        <eventSubURL>/event/connectionmanager</eventSubURL>
                      </service>
                      <service>
                        <serviceType>{DlnaConstants.ServiceTypeRenderingControl}</serviceType>
                        <serviceId>{DlnaConstants.ServiceIdRenderingControl}</serviceId>
                        <SCPDURL>/scpd/renderingcontrol.xml</SCPDURL>
                        <controlURL>/ctrl/renderingcontrol</controlURL>
                        <eventSubURL>/event/renderingcontrol</eventSubURL>
                      </service>
                    </serviceList>
                  </device>
                </root>
                """;
    }
}