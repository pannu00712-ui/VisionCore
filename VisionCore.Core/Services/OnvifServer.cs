using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisionCore.Core.Interfaces;
using VisionCore.Core.Models;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// ONVIF Profile S Server — Pure C# implementation.
    ///
    /// Architecture:
    ///   ┌─────────────────────────────────────────────────────┐
    ///   │  WsDiscoveryResponder  (UDP multicast 239.255.255.250:3702)  │
    ///   │    → responds to Probe messages with ProbeMatch               │
    ///   ├─────────────────────────────────────────────────────┤
    ///   │  OnvifHttpListener  (HTTP per-camera port e.g. 8080)         │
    ///   │    → /onvif/device_service    → DeviceService SOAP           │
    ///   │    → /onvif/media_service     → MediaService SOAP            │
    ///   │    → /onvif/events_service    → EventsService SOAP           │
    ///   │    → /onvif/ptz_service       → PTZ stub SOAP                │
    ///   │    → /snapshot/{id}           → JPEG snapshot HTTP           │
    ///   └─────────────────────────────────────────────────────┘
    ///
    /// Tested against: Milestone XProtect, Synology Surveillance Station,
    ///                 Blue Iris, iSpy, VLC, ONVIF Device Manager.
    /// </summary>
    public sealed class OnvifServer : IOnvifServer, IDisposable
    {
        private readonly ILogger<OnvifServer>  _logger;
        private readonly SettingsService       _settings;
        private WsDiscoveryResponder?          _discovery;
        private MultiChannelOnvifListener?     _multiChannel;

        // MultipleCamera mode — one listener per camera
        private readonly ConcurrentDictionary<Guid, OnvifDeviceListener> _listeners = new();
        private CancellationTokenSource? _cts;

        public OnvifServer(ILogger<OnvifServer> logger, SettingsService settings)
        {
            _logger   = logger;
            _settings = settings;
        }

        // ── IOnvifServer ──────────────────────────────────────────────────────

        public async Task StartAsync(CancellationToken ct = default)
        {
            _cts       = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _discovery = new WsDiscoveryResponder(_logger);
            await _discovery.StartAsync(_cts.Token);

            // In MultipleChannel mode, start the shared listener immediately.
            // Per-camera listeners are NOT started in this mode.
            if (_settings.App.OnvifMode == OnvifMode.MultipleChannel)
            {
                _multiChannel = new MultiChannelOnvifListener(
                    _settings.App.OnvifSharedPort, _logger);
                _multiChannel.Start();
                _logger.LogInformation(
                    "ONVIF MultipleChannel mode — shared listener on port {Port}.",
                    _settings.App.OnvifSharedPort);
            }
            else
            {
                _logger.LogInformation(
                    "ONVIF MultipleCamera mode — per-camera listeners will start on individual ports.");
            }

            _logger.LogInformation("ONVIF server started. WS-Discovery listening on UDP 3702.");
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_discovery    != null) await _discovery.StopAsync();
            if (_multiChannel != null) await _multiChannel.StopAsync();
            foreach (var l in _listeners.Values) await l.StopAsync();
            _listeners.Clear();
            _logger.LogInformation("ONVIF server stopped.");
        }

        public void RegisterDevice(CameraConfig config)
        {
            if (_settings.App.OnvifMode == OnvifMode.MultipleChannel)
            {
                // Add as a channel to the shared listener; no per-camera HTTP port needed.
                _multiChannel?.AddChannel(config);
                // WS-Discovery still advertises the single shared device (only once).
                if (_listeners.IsEmpty)
                    _discovery?.RegisterSharedDevice(_settings.App.OnvifSharedPort);
                _logger.LogInformation(
                    "ONVIF MultiChannel: channel added for '{Name}'.", config.Name);
                return;
            }

            // MultipleCamera mode — unchanged original behaviour
            if (_listeners.ContainsKey(config.Id)) return;
            var listener = new OnvifDeviceListener(config, _logger);
            listener.Start();
            _listeners[config.Id] = listener;
            _discovery?.RegisterDevice(config);
            _logger.LogInformation(
                "ONVIF device registered: '{Name}' on port {Port}.", config.Name, config.OnvifPort);
        }

        public void UnregisterDevice(Guid id)
        {
            if (_settings.App.OnvifMode == OnvifMode.MultipleChannel)
            {
                _multiChannel?.RemoveChannel(id);
                _logger.LogInformation("ONVIF MultiChannel: channel removed {Id}.", id);
                return;
            }

            if (!_listeners.TryRemove(id, out var l)) return;
            _ = l.StopAsync();
            _discovery?.UnregisterDevice(id);
            _logger.LogInformation("ONVIF device unregistered: {Id}", id);
        }

        /// <summary>
        /// Notify a registered ONVIF device that motion state has changed.
        /// Called by CameraManager when MotionDetector fires.
        /// </summary>
        public void NotifyMotion(Guid cameraId, bool isMotion)
        {
            if (_settings.App.OnvifMode == OnvifMode.MultipleChannel)
            {
                _multiChannel?.NotifyMotion(cameraId, isMotion);
                return;
            }
            if (_listeners.TryGetValue(cameraId, out var listener))
                listener.SetMotionState(isMotion);
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _cts?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WS-Discovery — UDP multicast responder
    // ══════════════════════════════════════════════════════════════════════════

    internal sealed class WsDiscoveryResponder
    {
        private readonly ILogger _logger;
        private System.Net.Sockets.UdpClient? _udp;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<Guid, CameraConfig> _devices = new();

        private static readonly IPEndPoint MulticastEndpoint =
            new(IPAddress.Parse("239.255.255.250"), 3702);

        public WsDiscoveryResponder(ILogger logger) => _logger = logger;

        public Task StartAsync(CancellationToken ct)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                _udp = new System.Net.Sockets.UdpClient();
                _udp.Client.SetSocketOption(
                    System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 3702));
                _udp.JoinMulticastGroup(IPAddress.Parse("239.255.255.250"));
                _udp.MulticastLoopback = false;

                _ = Task.Run(() => ListenLoopAsync(_cts.Token));
                _logger.LogDebug("WS-Discovery UDP listener bound on :3702");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not bind WS-Discovery UDP :3702. " +
                    "ONVIF auto-discovery will not work (manual IP entry still works).");
            }
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            try { _udp?.Close(); } catch { }
            return Task.CompletedTask;
        }

        public void RegisterDevice(CameraConfig cfg)   => _devices[cfg.Id] = cfg;
        public void UnregisterDevice(Guid id)           => _devices.TryRemove(id, out _);

        // ── MultipleChannel mode: single shared device ────────────────────────
        private static readonly Guid SharedDeviceId = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        public void RegisterSharedDevice(int port)
        {
            // Register a synthetic CameraConfig representing the shared ONVIF device.
            var shared = new CameraConfig
            {
                Id         = SharedDeviceId,
                Name       = "VisionCore",
                OnvifPort  = port,
                OnvifEnabled = true,
            };
            _devices[SharedDeviceId] = shared;
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result  = await _udp!.ReceiveAsync(ct);
                    var xml     = Encoding.UTF8.GetString(result.Buffer);

                    if (!xml.Contains("Probe")) continue;   // only respond to Probe

                    // Parse types filter — respond if NetworkVideoTransmitter or empty
                    bool wantsNVT = !xml.Contains("Types") ||
                                    xml.Contains("NetworkVideoTransmitter") ||
                                    xml.Contains("Device");
                    if (!wantsNVT) continue;

                    var msgId = ExtractMessageId(xml);

                    foreach (var cfg in _devices.Values)
                    {
                        var response = BuildProbeMatch(cfg, msgId);
                        var bytes    = Encoding.UTF8.GetBytes(response);
                        await _udp.SendAsync(bytes, bytes.Length, result.RemoteEndPoint);
                        _logger.LogDebug("WS-Discovery ProbeMatch sent for '{Name}' to {EP}",
                            cfg.Name, result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "WS-Discovery receive error.");
                    await Task.Delay(500, ct);
                }
            }
        }

        private static string ExtractMessageId(string xml)
        {
            const string open  = "<wsa:MessageID>";
            const string close = "</wsa:MessageID>";
            var s = xml.IndexOf(open,  StringComparison.Ordinal);
            var e = xml.IndexOf(close, StringComparison.Ordinal);
            if (s < 0 || e < 0) return "uuid:" + Guid.NewGuid();
            return xml.Substring(s + open.Length, e - s - open.Length);
        }

        private static string BuildProbeMatch(CameraConfig cfg, string relatesTo)
        {
            var localIp = GetLocalIp();
            var xAddrs  = $"http://{localIp}:{cfg.OnvifPort}/onvif/device_service";
            var urn      = $"urn:uuid:{cfg.Id}";

            return $"""
<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
            xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing"
            xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery"
            xmlns:dn="http://www.onvif.org/ver10/network/wsdl">
  <s:Header>
    <a:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches</a:Action>
    <a:MessageID>uuid:{Guid.NewGuid()}</a:MessageID>
    <a:RelatesTo>{relatesTo}</a:RelatesTo>
    <a:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:To>
  </s:Header>
  <s:Body>
    <d:ProbeMatches>
      <d:ProbeMatch>
        <a:EndpointReference><a:Address>{urn}</a:Address></a:EndpointReference>
        <d:Types>dn:NetworkVideoTransmitter</d:Types>
        <d:Scopes>
          onvif://www.onvif.org/type/video_encoder
          onvif://www.onvif.org/type/audio_encoder
          onvif://www.onvif.org/hardware/VisionCore
          onvif://www.onvif.org/name/{Uri.EscapeDataString(cfg.Name)}
          onvif://www.onvif.org/location/
        </d:Scopes>
        <d:XAddrs>{xAddrs}</d:XAddrs>
        <d:MetadataVersion>1</d:MetadataVersion>
      </d:ProbeMatch>
    </d:ProbeMatches>
  </s:Body>
</s:Envelope>
""";
        }

        internal static string GetLocalIp()
        {
            try
            {
                using var s = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram, 0);
                s.Connect("8.8.8.8", 65530);
                return (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Per-Camera HTTP Listener — SOAP endpoint
    // ══════════════════════════════════════════════════════════════════════════

    internal sealed class OnvifDeviceListener
    {
        private readonly CameraConfig _cfg;
        private readonly ILogger _logger;
        private HttpListener? _http;
        private CancellationTokenSource? _cts;
        private volatile bool _motionActive;
        private DateTime _motionLastSet = DateTime.MinValue;

        /// <summary>Called by OnvifServer.NotifyMotion — thread-safe.</summary>
        public void SetMotionState(bool isMotion)
        {
            _motionActive  = isMotion;
            _motionLastSet = DateTime.UtcNow;
        }

        public OnvifDeviceListener(CameraConfig cfg, ILogger logger)
        {
            _cfg    = cfg;
            _logger = logger;
        }

        public void Start()
        {
            _cts  = new CancellationTokenSource();
            _http = new HttpListener();
            _http.Prefixes.Add($"http://+:{_cfg.OnvifPort}/onvif/");
            _http.Prefixes.Add($"http://+:{_cfg.OnvifPort}/snapshot/");

            // Basic auth
            if (!string.IsNullOrEmpty(_cfg.Username))
                _http.AuthenticationSchemes = AuthenticationSchemes.Basic;
            else
                _http.AuthenticationSchemes = AuthenticationSchemes.Anonymous;

            try
            {
                _http.Start();
                _ = Task.Run(() => RequestLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to start ONVIF HTTP listener on port {Port}. " +
                    "Run as administrator or use netsh to reserve the URL.",
                    _cfg.OnvifPort);
            }
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            try { _http?.Stop(); } catch { }
            await Task.CompletedTask;
        }

        private async Task RequestLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && (_http?.IsListening ?? false))
            {
                try
                {
                    var ctx  = await _http!.GetContextAsync();
                    _        = Task.Run(() => HandleRequestAsync(ctx), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "ONVIF HTTP listener error.");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            var req  = ctx.Request;
            var resp = ctx.Response;

            // Auth check
            if (!string.IsNullOrEmpty(_cfg.Username) &&
                !CheckBasicAuth(ctx))
            {
                resp.StatusCode = 401;
                resp.AddHeader("WWW-Authenticate", "Basic realm=\"VisionCore\"");
                resp.Close();
                return;
            }

            try
            {
                var path = req.Url?.AbsolutePath ?? "/";
                _logger.LogDebug("ONVIF request: {Method} {Path}", req.HttpMethod, path);

                string body;
                if (path.StartsWith("/snapshot/", StringComparison.OrdinalIgnoreCase))
                {
                    await ServeSnapshotAsync(ctx);
                    return;
                }

                // Read SOAP body
                using var reader = new System.IO.StreamReader(req.InputStream, req.ContentEncoding);
                var soap = await reader.ReadToEndAsync();

                body = path switch
                {
                    _ when path.Contains("device_service") => HandleDeviceService(soap),
                    _ when path.Contains("media_service")  => HandleMediaService(soap),
                    _ when path.Contains("events_service") => HandleEventsService(soap),
                    _ when path.Contains("ptz_service")    => HandlePtzService(soap),
                    _ => SoapFault("UnknownAction", $"Unknown ONVIF endpoint: {path}")
                };

                var bytes = Encoding.UTF8.GetBytes(body);
                resp.ContentType     = "application/soap+xml; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                resp.StatusCode      = 200;
                await resp.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling ONVIF request.");
                var fault  = Encoding.UTF8.GetBytes(SoapFault("InternalError", ex.Message));
                resp.StatusCode      = 500;
                resp.ContentLength64 = fault.Length;
                await resp.OutputStream.WriteAsync(fault);
            }
            finally
            {
                resp.Close();
            }
        }

        // ── Device Service ────────────────────────────────────────────────────

        private string HandleDeviceService(string soap)
        {
            if (soap.Contains("GetDeviceInformation")) return GetDeviceInformation();
            if (soap.Contains("GetCapabilities"))      return GetCapabilities();
            if (soap.Contains("GetServices"))          return GetServices();
            if (soap.Contains("GetServiceCapabilities"))return GetServiceCapabilities();
            if (soap.Contains("GetSystemDateAndTime")) return GetSystemDateAndTime();
            if (soap.Contains("GetUsers"))             return GetUsers();
            if (soap.Contains("GetNetworkInterfaces")) return GetNetworkInterfaces();
            if (soap.Contains("GetHostname"))          return GetHostname();
            if (soap.Contains("SystemReboot"))         return SystemReboot();

            return SoapFault("ActionNotSupported", "Device service action not implemented.");
        }

        private string GetDeviceInformation()
        {
            return SoapEnvelope($"""
<tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
  <tds:Manufacturer>VisionCore</tds:Manufacturer>
  <tds:Model>VirtualCamera</tds:Model>
  <tds:FirmwareVersion>1.0.0</tds:FirmwareVersion>
  <tds:SerialNumber>VC-{_cfg.Id.ToString()[..8].ToUpper()}</tds:SerialNumber>
  <tds:HardwareId>VisionCore-WPF-NET8</tds:HardwareId>
</tds:GetDeviceInformationResponse>
""");
        }

        private string GetCapabilities()
        {
            var localIp  = WsDiscoveryResponder.GetLocalIp();
            var baseUrl  = $"http://{localIp}:{_cfg.OnvifPort}/onvif";
            return SoapEnvelope($"""
<tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                              xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:Capabilities>
    <tt:Analytics/>
    <tt:Device>
      <tt:XAddr>{baseUrl}/device_service</tt:XAddr>
      <tt:Network><tt:IPFilter>false</tt:IPFilter><tt:ZeroConfiguration>false</tt:ZeroConfiguration>
        <tt:IPVersion6>false</tt:IPVersion6><tt:DynDNS>false</tt:DynDNS></tt:Network>
      <tt:System><tt:DiscoveryResolve>false</tt:DiscoveryResolve>
        <tt:DiscoveryBye>true</tt:DiscoveryBye><tt:RemoteDiscovery>false</tt:RemoteDiscovery>
        <tt:SystemBackup>false</tt:SystemBackup><tt:SystemLogging>false</tt:SystemLogging>
        <tt:FirmwareUpgrade>false</tt:FirmwareUpgrade>
        <tt:SupportedVersions><tt:Major>2</tt:Major><tt:Minor>0</tt:Minor></tt:SupportedVersions>
      </tt:System>
    </tt:Device>
    <tt:Events>
      <tt:XAddr>{baseUrl}/events_service</tt:XAddr>
      <tt:WSSubscriptionPolicySupport>true</tt:WSSubscriptionPolicySupport>
      <tt:WSPullPointSupport>true</tt:WSPullPointSupport>
    </tt:Events>
    <tt:Imaging/>
    <tt:Media>
      <tt:XAddr>{baseUrl}/media_service</tt:XAddr>
      <tt:StreamingCapabilities>
        <tt:RTPMulticast>false</tt:RTPMulticast>
        <tt:RTP_TCP>true</tt:RTP_TCP>
        <tt:RTP_RTSP_TCP>true</tt:RTP_RTSP_TCP>
      </tt:StreamingCapabilities>
    </tt:Media>
    <tt:PTZ><tt:XAddr>{baseUrl}/ptz_service</tt:XAddr></tt:PTZ>
  </tds:Capabilities>
</tds:GetCapabilitiesResponse>
""");
        }

        private string GetServices()
        {
            var localIp = WsDiscoveryResponder.GetLocalIp();
            var b       = $"http://{localIp}:{_cfg.OnvifPort}/onvif";
            return SoapEnvelope($"""
<tds:GetServicesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
  <tds:Service>
    <tds:Namespace>http://www.onvif.org/ver10/device/wsdl</tds:Namespace>
    <tds:XAddr>{b}/device_service</tds:XAddr>
    <tds:Version><tds:Major>2</tds:Major><tds:Minor>0</tds:Minor></tds:Version>
  </tds:Service>
  <tds:Service>
    <tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace>
    <tds:XAddr>{b}/media_service</tds:XAddr>
    <tds:Version><tds:Major>2</tds:Major><tds:Minor>0</tds:Minor></tds:Version>
  </tds:Service>
  <tds:Service>
    <tds:Namespace>http://www.onvif.org/ver20/events/wsdl</tds:Namespace>
    <tds:XAddr>{b}/events_service</tds:XAddr>
    <tds:Version><tds:Major>2</tds:Major><tds:Minor>0</tds:Minor></tds:Version>
  </tds:Service>
  <tds:Service>
    <tds:Namespace>http://www.onvif.org/ver20/ptz/wsdl</tds:Namespace>
    <tds:XAddr>{b}/ptz_service</tds:XAddr>
    <tds:Version><tds:Major>2</tds:Major><tds:Minor>0</tds:Minor></tds:Version>
  </tds:Service>
</tds:GetServicesResponse>
""");
        }

        private string GetServiceCapabilities() => SoapEnvelope("""
<tds:GetServiceCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                                     xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:Capabilities>
    <tds:Network IPFilter="false" ZeroConfiguration="false" IPVersion6="false" DynDNS="false"
                 Dot11Configuration="false" Dot1XConfigurations="0" HostnameFromDHCP="false"
                 NTP="0" DHCPv6="false"/>
    <tds:Security TLS1.0="false" TLS1.1="false" TLS1.2="false" OnboardKeyGeneration="false"
                  AccessPolicyConfig="false" DefaultAccessPolicy="false" Dot1X="false"
                  RemoteUserHandling="false" X.509Token="false" SAMLToken="false"
                  KerberosToken="false" UsernameToken="true" HttpDigest="false"
                  RELToken="false" MaxUsers="1" SupportedEAPMethods="0"/>
    <tds:System DiscoveryResolve="false" DiscoveryBye="true" RemoteDiscovery="false"
                SystemBackup="false" SystemLogging="false" FirmwareUpgrade="false"
                HttpFirmwareUpgrade="false" HttpSystemBackup="false"
                HttpSystemLogging="false" HttpSupportInformation="false"/>
  </tds:Capabilities>
</tds:GetServiceCapabilitiesResponse>
""");

        private string GetSystemDateAndTime()
        {
            var now = DateTime.UtcNow;
            return SoapEnvelope($"""
<tds:GetSystemDateAndTimeResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                                   xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:SystemDateAndTime>
    <tt:DateTimeType>NTP</tt:DateTimeType>
    <tt:DaylightSavings>false</tt:DaylightSavings>
    <tt:TimeZone><tt:TZ>UTC</tt:TZ></tt:TimeZone>
    <tt:UTCDateTime>
      <tt:Time><tt:Hour>{now.Hour}</tt:Hour><tt:Minute>{now.Minute}</tt:Minute><tt:Second>{now.Second}</tt:Second></tt:Time>
      <tt:Date><tt:Year>{now.Year}</tt:Year><tt:Month>{now.Month}</tt:Month><tt:Day>{now.Day}</tt:Day></tt:Date>
    </tt:UTCDateTime>
  </tds:SystemDateAndTime>
</tds:GetSystemDateAndTimeResponse>
""");
        }

        private string GetUsers() => SoapEnvelope($"""
<tds:GetUsersResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                       xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:User>
    <tt:Username>{_cfg.Username ?? "admin"}</tt:Username>
    <tt:UserLevel>Administrator</tt:UserLevel>
  </tds:User>
</tds:GetUsersResponse>
""");

        private string GetNetworkInterfaces()
        {
            var localIp = WsDiscoveryResponder.GetLocalIp();
            return SoapEnvelope($"""
<tds:GetNetworkInterfacesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                                   xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:NetworkInterfaces token="eth0">
    <tt:Enabled>true</tt:Enabled>
    <tt:IPv4>
      <tt:Enabled>true</tt:Enabled>
      <tt:Config>
        <tt:Manual><tt:Address>{localIp}</tt:Address><tt:PrefixLength>24</tt:PrefixLength></tt:Manual>
        <tt:DHCP>false</tt:DHCP>
      </tt:Config>
    </tt:IPv4>
  </tds:NetworkInterfaces>
</tds:GetNetworkInterfacesResponse>
""");
        }

        private string GetHostname() => SoapEnvelope($"""
<tds:GetHostnameResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                          xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:HostnameInformation>
    <tt:FromDHCP>false</tt:FromDHCP>
    <tt:Name>{Environment.MachineName}</tt:Name>
  </tds:HostnameInformation>
</tds:GetHostnameResponse>
""");

        private string SystemReboot() => SoapEnvelope("""
<tds:SystemRebootResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
  <tds:Message>Reboot not supported on virtual device.</tds:Message>
</tds:SystemRebootResponse>
""");

        // ── Media Service ─────────────────────────────────────────────────────

        private string HandleMediaService(string soap)
        {
            if (soap.Contains("GetProfiles"))              return GetProfiles();
            if (soap.Contains("GetProfile"))               return GetProfile();
            if (soap.Contains("GetStreamUri"))             return GetStreamUri();
            if (soap.Contains("GetSnapshotUri"))           return GetSnapshotUri();
            if (soap.Contains("GetVideoSources"))          return GetVideoSources();
            if (soap.Contains("GetVideoSourceConfigurations")) return GetVideoSourceConfigurations();
            if (soap.Contains("GetVideoEncoderConfigurations")) return GetVideoEncoderConfigurations();
            if (soap.Contains("GetVideoEncoderConfigurationOptions")) return GetVideoEncoderConfigurationOptions();
            if (soap.Contains("GetAudioSources"))          return GetAudioSources();
            if (soap.Contains("GetCompatibleVideoEncoderConfigurations")) return GetVideoEncoderConfigurations();

            return SoapFault("ActionNotSupported", "Media service action not implemented.");
        }

        private string GetProfiles()
        {
            var (w, h) = ResolutionPixels();
            var codec  = _cfg.Codec == VideoCodec.H265 ? "H265" : "H264";
            return SoapEnvelope($"""
<trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                          xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:Profiles token="profile_main" fixed="true">
    <tt:Name>{_cfg.Name}</tt:Name>
    <tt:VideoSourceConfiguration token="vsc_main">
      <tt:Name>VideoSource</tt:Name><tt:UseCount>1</tt:UseCount>
      <tt:SourceToken>vst_main</tt:SourceToken>
      <tt:Bounds x="0" y="0" width="{w}" height="{h}"/>
    </tt:VideoSourceConfiguration>
    <tt:VideoEncoderConfiguration token="vec_main">
      <tt:Name>VideoEncoder</tt:Name><tt:UseCount>1</tt:UseCount>
      <tt:Encoding>{codec}</tt:Encoding>
      <tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution>
      <tt:Quality>5</tt:Quality>
      <tt:RateControl>
        <tt:FrameRateLimit>{_cfg.FrameRate}</tt:FrameRateLimit>
        <tt:EncodingInterval>1</tt:EncodingInterval>
        <tt:BitrateLimit>{_cfg.Bitrate}</tt:BitrateLimit>
      </tt:RateControl>
      <tt:H264><tt:GovLength>{_cfg.FrameRate * 2}</tt:GovLength><tt:H264Profile>High</tt:H264Profile></tt:H264>
      <tt:Multicast><tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>
        <tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart></tt:Multicast>
      <tt:SessionTimeout>PT60S</tt:SessionTimeout>
    </tt:VideoEncoderConfiguration>
    {AudioEncoderXml()}
    <tt:PTZConfiguration token="ptz_main">
      <tt:Name>PTZConfig</tt:Name><tt:UseCount>1</tt:UseCount>
      <tt:NodeToken>ptz_node</tt:NodeToken>
      <tt:DefaultAbsolutePantTiltPositionSpace>http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace</tt:DefaultAbsolutePantTiltPositionSpace>
      <tt:DefaultAbsoluteZoomPositionSpace>http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace</tt:DefaultAbsoluteZoomPositionSpace>
      <tt:DefaultPTZSpeed><tt:PanTilt x="0.5" y="0.5" space="http://www.onvif.org/ver10/tptz/PanTiltSpaces/GenericSpeedSpace"/>
        <tt:Zoom x="0.5" space="http://www.onvif.org/ver10/tptz/ZoomSpaces/ZoomGenericSpeedSpace"/></tt:DefaultPTZSpeed>
      <tt:DefaultPTZTimeout>PT5S</tt:DefaultPTZTimeout>
    </tt:PTZConfiguration>
  </trt:Profiles>
</trt:GetProfilesResponse>
""");
        }

        private string GetProfile() => GetProfiles()
            .Replace("GetProfilesResponse", "GetProfileResponse");

        private string GetStreamUri()
        {
            var clientUrl = RtspStreamManager.GetClientUrl(_cfg);
            return SoapEnvelope($"""
<trt:GetStreamUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                           xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:MediaUri>
    <tt:Uri>{clientUrl}</tt:Uri>
    <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
    <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
    <tt:Timeout>PT60S</tt:Timeout>
  </trt:MediaUri>
</trt:GetStreamUriResponse>
""");
        }

        private string GetSnapshotUri()
        {
            var localIp = WsDiscoveryResponder.GetLocalIp();
            var url     = $"http://{localIp}:{_cfg.OnvifPort}/snapshot/{_cfg.Id}";
            return SoapEnvelope($"""
<trt:GetSnapshotUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                             xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:MediaUri>
    <tt:Uri>{url}</tt:Uri>
    <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
    <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
    <tt:Timeout>PT60S</tt:Timeout>
  </trt:MediaUri>
</trt:GetSnapshotUriResponse>
""");
        }

        private string GetVideoSources()
        {
            var (w, h) = ResolutionPixels();
            return SoapEnvelope($"""
<trt:GetVideoSourcesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                              xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:VideoSources token="vst_main">
    <tt:Framerate>{_cfg.FrameRate}</tt:Framerate>
    <tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution>
  </trt:VideoSources>
</trt:GetVideoSourcesResponse>
""");
        }

        private string GetVideoSourceConfigurations()
        {
            var (w, h) = ResolutionPixels();
            return SoapEnvelope($"""
<trt:GetVideoSourceConfigurationsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                           xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:Configurations token="vsc_main">
    <tt:Name>VideoSource</tt:Name><tt:UseCount>1</tt:UseCount>
    <tt:SourceToken>vst_main</tt:SourceToken>
    <tt:Bounds x="0" y="0" width="{w}" height="{h}"/>
  </trt:Configurations>
</trt:GetVideoSourceConfigurationsResponse>
""");
        }

        private string GetVideoEncoderConfigurations()
        {
            var (w, h) = ResolutionPixels();
            var codec  = _cfg.Codec == VideoCodec.H265 ? "H265" : "H264";
            return SoapEnvelope($"""
<trt:GetVideoEncoderConfigurationsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                            xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:Configurations token="vec_main">
    <tt:Name>VideoEncoder</tt:Name><tt:UseCount>1</tt:UseCount>
    <tt:Encoding>{codec}</tt:Encoding>
    <tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution>
    <tt:Quality>5</tt:Quality>
    <tt:RateControl>
      <tt:FrameRateLimit>{_cfg.FrameRate}</tt:FrameRateLimit>
      <tt:EncodingInterval>1</tt:EncodingInterval>
      <tt:BitrateLimit>{_cfg.Bitrate}</tt:BitrateLimit>
    </tt:RateControl>
    <tt:H264><tt:GovLength>{_cfg.FrameRate * 2}</tt:GovLength><tt:H264Profile>High</tt:H264Profile></tt:H264>
    <tt:SessionTimeout>PT60S</tt:SessionTimeout>
  </trt:Configurations>
</trt:GetVideoEncoderConfigurationsResponse>
""");
        }

        private string GetVideoEncoderConfigurationOptions()
        {
            return SoapEnvelope("""
<trt:GetVideoEncoderConfigurationOptionsResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                                                  xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:Options>
    <tt:QualityRange><tt:Min>1</tt:Min><tt:Max>10</tt:Max></tt:QualityRange>
    <tt:H264>
      <tt:ResolutionsAvailable><tt:Width>1920</tt:Width><tt:Height>1080</tt:Height></tt:ResolutionsAvailable>
      <tt:ResolutionsAvailable><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:ResolutionsAvailable>
      <tt:ResolutionsAvailable><tt:Width>3840</tt:Width><tt:Height>2160</tt:Height></tt:ResolutionsAvailable>
      <tt:GovLengthRange><tt:Min>1</tt:Min><tt:Max>300</tt:Max></tt:GovLengthRange>
      <tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>60</tt:Max></tt:FrameRateRange>
      <tt:EncodingIntervalRange><tt:Min>1</tt:Min><tt:Max>1</tt:Max></tt:EncodingIntervalRange>
      <tt:H264ProfilesSupported>Baseline</tt:H264ProfilesSupported>
      <tt:H264ProfilesSupported>Main</tt:H264ProfilesSupported>
      <tt:H264ProfilesSupported>High</tt:H264ProfilesSupported>
    </tt:H264>
  </trt:Options>
</trt:GetVideoEncoderConfigurationOptionsResponse>
""");
        }

        private string GetAudioSources()
        {
            if (_cfg.AudioSource == AudioSource.None)
                return SoapEnvelope("""<trt:GetAudioSourcesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"/>""");

            return SoapEnvelope("""
<trt:GetAudioSourcesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                              xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:AudioSources token="ast_main">
    <tt:Channels>1</tt:Channels>
  </trt:AudioSources>
</trt:GetAudioSourcesResponse>
""");
        }

        private string AudioEncoderXml()
        {
            if (_cfg.AudioSource == AudioSource.None) return "";
            return $"""
    <tt:AudioSourceConfiguration token="asc_main">
      <tt:Name>AudioSource</tt:Name><tt:UseCount>1</tt:UseCount>
      <tt:SourceToken>ast_main</tt:SourceToken>
    </tt:AudioSourceConfiguration>
    <tt:AudioEncoderConfiguration token="aec_main">
      <tt:Name>AudioEncoder</tt:Name><tt:UseCount>1</tt:UseCount>
      <tt:Encoding>AAC</tt:Encoding>
      <tt:Bitrate>{_cfg.AudioBitrate}</tt:Bitrate>
      <tt:SampleRate>44100</tt:SampleRate>
      <tt:SessionTimeout>PT60S</tt:SessionTimeout>
    </tt:AudioEncoderConfiguration>
""";
        }

        // ── Events Service ────────────────────────────────────────────────────

        private string HandleEventsService(string soap)
        {
            if (soap.Contains("GetEventProperties"))  return GetEventProperties();
            if (soap.Contains("CreatePullPointSubscription")) return CreatePullPointSubscription();
            if (soap.Contains("PullMessages"))        return PullMessages();
            if (soap.Contains("Renew"))               return RenewSubscription();
            if (soap.Contains("Unsubscribe"))         return UnsubscribeResponse();

            return SoapFault("ActionNotSupported", "Events action not implemented.");
        }

        private string GetEventProperties() => SoapEnvelope("""
<tev:GetEventPropertiesResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                                 xmlns:tt="http://www.onvif.org/ver10/schema"
                                 xmlns:tns1="http://www.onvif.org/ver10/topics">
  <tev:TopicNamespaceLocation>http://www.onvif.org/onvif/ver10/topics/topicns.xml</tev:TopicNamespaceLocation>
  <tev:FixedTopicSet>true</tev:FixedTopicSet>
  <tev:TopicSet>
    <tns1:RuleEngine>
      <tns1:MotionRegionDetector>
        <tns1:Motion wstop:topic="true" xmlns:wstop="http://docs.oasis-open.org/wsn/t-1">
          <tt:MessageDescription IsProperty="false">
            <tt:Source><tt:SimpleItemDescription Name="VideoSourceConfigurationToken" Type="tt:ReferenceToken"/></tt:Source>
            <tt:Data><tt:SimpleItemDescription Name="IsMotion" Type="xsd:boolean"/></tt:Data>
          </tt:MessageDescription>
        </tns1:Motion>
      </tns1:MotionRegionDetector>
    </tns1:RuleEngine>
  </tev:TopicSet>
</tev:GetEventPropertiesResponse>
""");

        private string CreatePullPointSubscription()
        {
            var localIp = WsDiscoveryResponder.GetLocalIp();
            var subAddr = $"http://{localIp}:{_cfg.OnvifPort}/onvif/events_service";
            var expires = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
            return SoapEnvelope($"""
<tev:CreatePullPointSubscriptionResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                                          xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing"
                                          xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
  <tev:SubscriptionReference>
    <wsa:Address>{subAddr}</wsa:Address>
  </tev:SubscriptionReference>
  <tev:CurrentTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</tev:CurrentTime>
  <tev:TerminationTime>{expires}</tev:TerminationTime>
</tev:CreatePullPointSubscriptionResponse>
""");
        }

        private string PullMessages()
        {
            // Emit a real motion event if state was updated in the last 5 s; otherwise empty.
            var now     = DateTime.UtcNow;
            var motionXml = string.Empty;

            if ((now - _motionLastSet).TotalSeconds < 5)
            {
                var val = _motionActive ? "true" : "false";
                motionXml = $"""
  <wsnt:NotificationMessage>
    <wsnt:Topic Dialect="http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet">
      tns1:RuleEngine/MotionRegionDetector/Motion
    </wsnt:Topic>
    <wsnt:Message>
      <tt:Message UtcTime="{now:yyyy-MM-ddTHH:mm:ssZ}" PropertyOperation="Changed">
        <tt:Source/>
        <tt:Key/>
        <tt:Data>
          <tt:SimpleItem Name="IsMotion" Value="{val}"/>
        </tt:Data>
      </tt:Message>
    </wsnt:Message>
  </wsnt:NotificationMessage>
""";
            }

            return SoapEnvelope($"""
<tev:PullMessagesResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                           xmlns:tt="http://www.onvif.org/ver10/schema"
                           xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
  <tev:CurrentTime>{now:yyyy-MM-ddTHH:mm:ssZ}</tev:CurrentTime>
  <tev:TerminationTime>{now.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}</tev:TerminationTime>
  {motionXml}
</tev:PullMessagesResponse>
""");
        }

        private string RenewSubscription() => SoapEnvelope($"""
<wsnt:RenewResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
  <wsnt:TerminationTime>{DateTime.UtcNow.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}</wsnt:TerminationTime>
  <wsnt:CurrentTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</wsnt:CurrentTime>
</wsnt:RenewResponse>
""");

        private string UnsubscribeResponse() => SoapEnvelope("""
<wsnt:UnsubscribeResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2"/>
""");

        // ── PTZ Service (stub — virtual cameras can't physically move) ─────────

        private string HandlePtzService(string soap)
        {
            if (soap.Contains("GetNodes"))        return GetPtzNodes();
            if (soap.Contains("GetConfigurations"))return GetPtzConfigurations();
            if (soap.Contains("ContinuousMove") ||
                soap.Contains("AbsoluteMove")   ||
                soap.Contains("RelativeMove"))    return PtzMoveStub();
            if (soap.Contains("Stop"))            return PtzStopStub();
            if (soap.Contains("GetStatus"))       return GetPtzStatus();
            if (soap.Contains("GetPresets"))      return GetPtzPresets();

            return SoapFault("ActionNotSupported", "PTZ action not implemented.");
        }

        private string GetPtzNodes() => SoapEnvelope("""
<tptz:GetNodesResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                        xmlns:tt="http://www.onvif.org/ver10/schema">
  <tptz:PTZNode token="ptz_node" FixedHomePosition="false">
    <tt:Name>VirtualPTZ</tt:Name>
    <tt:SupportedPTZSpaces>
      <tt:AbsolutePanTiltPositionSpace>
        <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace</tt:URI>
        <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
        <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
      </tt:AbsolutePanTiltPositionSpace>
      <tt:ContinuousPanTiltVelocitySpace>
        <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/GenericSpeedSpace</tt:URI>
        <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
        <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
      </tt:ContinuousPanTiltVelocitySpace>
    </tt:SupportedPTZSpaces>
    <tt:MaximumNumberOfPresets>10</tt:MaximumNumberOfPresets>
    <tt:HomeSupported>false</tt:HomeSupported>
  </tptz:PTZNode>
</tptz:GetNodesResponse>
""");

        private string GetPtzConfigurations() => SoapEnvelope("""
<tptz:GetConfigurationsResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                                 xmlns:tt="http://www.onvif.org/ver10/schema">
  <tptz:PTZConfiguration token="ptz_main">
    <tt:Name>PTZConfig</tt:Name><tt:UseCount>1</tt:UseCount>
    <tt:NodeToken>ptz_node</tt:NodeToken>
    <tt:DefaultPTZTimeout>PT5S</tt:DefaultPTZTimeout>
  </tptz:PTZConfiguration>
</tptz:GetConfigurationsResponse>
""");

        private string PtzMoveStub() => SoapEnvelope("""
<tptz:ContinuousMoveResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"/>
""");

        private string PtzStopStub() => SoapEnvelope("""
<tptz:StopResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"/>
""");

        private string GetPtzStatus() => SoapEnvelope("""
<tptz:GetStatusResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"
                         xmlns:tt="http://www.onvif.org/ver10/schema">
  <tptz:PTZStatus>
    <tt:Position>
      <tt:PanTilt x="0" y="0" space="http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace"/>
      <tt:Zoom x="0" space="http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace"/>
    </tt:Position>
    <tt:MoveStatus>
      <tt:PanTilt>IDLE</tt:PanTilt>
      <tt:Zoom>IDLE</tt:Zoom>
    </tt:MoveStatus>
  </tptz:PTZStatus>
</tptz:GetStatusResponse>
""");

        private string GetPtzPresets() => SoapEnvelope("""
<tptz:GetPresetsResponse xmlns:tptz="http://www.onvif.org/ver20/ptz/wsdl"/>
""");

        // ── Snapshot endpoint ─────────────────────────────────────────────────

        private async Task ServeSnapshotAsync(HttpListenerContext ctx)
        {
            // Grab a single frame from FFmpeg via pipe and return as JPEG.
            // Falls back to a 1x1 placeholder if FFmpeg isn't running.
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.StatusCode  = 200;

            try
            {
                var (w, h) = ResolutionPixels();
                var ffArgs = $"-f gdigrab -framerate 1 -i desktop -vframes 1 " +
                             $"-vf scale={w}:{h} -f mjpeg pipe:1";

                using var proc = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName               = "ffmpeg",
                        Arguments              = ffArgs,
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                    }
                };
                proc.Start();
                var jpeg = await proc.StandardOutput.BaseStream
                    .ReadAllBytesAsync(CancellationToken.None);
                proc.WaitForExit(3000);

                ctx.Response.ContentLength64 = jpeg.Length;
                await ctx.Response.OutputStream.WriteAsync(jpeg);
            }
            catch
            {
                // Return 1×1 transparent placeholder JPEG
                var placeholder = Convert.FromBase64String(
                    "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8U" +
                    "HRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAARC" +
                    "AABAAEDASIA//EABIAAAQEBAQEBAQAAAAAAAAAAAAQDBAUGB//EABQQAQAAAAAAAAAAAAAAAAAAAAD" +
                    "//EABQBAQAAAAAAAAAAAAAAAAAAAAD//EABQRAQAAAAAAAAAAAAAAAAAAAAD/2gAMAwEAAhEDEQA/AKwAB//Z");
                ctx.Response.ContentLength64 = placeholder.Length;
                await ctx.Response.OutputStream.WriteAsync(placeholder);
            }
            finally
            {
                ctx.Response.Close();
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string SoapEnvelope(string body) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
            xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
            xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <s:Body>{body}</s:Body>
</s:Envelope>
""";

        private static string SoapFault(string code, string reason) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
  <s:Body>
    <s:Fault>
      <s:Code><s:Value>s:Sender</s:Value>
        <s:Subcode><s:Value>{code}</s:Value></s:Subcode>
      </s:Code>
      <s:Reason><s:Text xml:lang="en">{System.Security.SecurityElement.Escape(reason)}</s:Text></s:Reason>
    </s:Fault>
  </s:Body>
</s:Envelope>
""";

        private bool CheckBasicAuth(HttpListenerContext ctx)
        {
            if (ctx.User?.Identity is not HttpListenerBasicIdentity identity) return false;
            return identity.Name == (_cfg.Username ?? "admin") &&
                   identity.Password == (_cfg.Password ?? "");
        }

        private (int w, int h) ResolutionPixels() => _cfg.Resolution switch
        {
            Resolution.R720p => (1280,  720),
            Resolution.R4K   => (3840, 2160),
            _                => (1920, 1080),
        };
    }


    // ══════════════════════════════════════════════════════════════════════════
    // MultiChannelOnvifListener — One ONVIF device, N profiles (channels)
    // Mirrors DeskCamera "Multiple Channels" mode.
    // All cameras share a single HTTP port; the NVR/VMS sees one IP camera
    // with multiple video sources / profiles.
    // ══════════════════════════════════════════════════════════════════════════

    internal sealed class MultiChannelOnvifListener
    {
        private readonly int      _port;
        private readonly ILogger  _logger;
        private HttpListener?     _http;
        private CancellationTokenSource? _cts;

        // Thread-safe channel registry keyed by CameraConfig.Id
        private readonly ConcurrentDictionary<Guid, CameraConfig> _channels = new();
        private readonly ConcurrentDictionary<Guid, (bool active, DateTime lastSet)> _motion = new();

        public MultiChannelOnvifListener(int port, ILogger logger)
        {
            _port   = port;
            _logger = logger;
        }

        // ── Channel management ─────────────────────────────────────────────

        public void AddChannel(CameraConfig cfg)    => _channels[cfg.Id] = cfg;
        public void RemoveChannel(Guid id)          { _channels.TryRemove(id, out _); _motion.TryRemove(id, out _); }
        public void NotifyMotion(Guid id, bool val) => _motion[id] = (val, DateTime.UtcNow);

        // ── HTTP lifecycle ─────────────────────────────────────────────────

        public void Start()
        {
            _cts  = new CancellationTokenSource();
            _http = new HttpListener();
            _http.Prefixes.Add($"http://+:{_port}/onvif/");
            _http.Prefixes.Add($"http://+:{_port}/snapshot/");
            _http.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
            try
            {
                _http.Start();
                _ = Task.Run(() => RequestLoopAsync(_cts.Token));
                _logger.LogInformation(
                    "MultiChannel ONVIF listener started on port {Port}.", _port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to start MultiChannel ONVIF listener on port {Port}. " +
                    "Run as administrator or use netsh to reserve the URL.", _port);
            }
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            try { _http?.Stop(); } catch { }
            await Task.CompletedTask;
        }

        // ── Request loop ──────────────────────────────────────────────────

        private async Task RequestLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _http!.GetContextAsync().WaitAsync(ct); }
                catch { break; }
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            try
            {
                var req  = ctx.Request;
                var resp = ctx.Response;
                var path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "";

                byte[] body;
                using (var ms = new System.IO.MemoryStream())
                {
                    await req.InputStream.CopyToAsync(ms);
                    body = ms.ToArray();
                }
                var soap = System.Text.Encoding.UTF8.GetString(body);

                string responseXml;
                if (path.Contains("device_service") || path.Contains("device"))
                    responseXml = HandleDeviceService(soap);
                else if (path.Contains("media"))
                    responseXml = HandleMediaService(soap);
                else if (path.Contains("events") || path.Contains("event"))
                    responseXml = HandleEventsService(soap);
                else if (path.Contains("snapshot"))
                    responseXml = string.Empty; // snapshot stub
                else
                    responseXml = SoapFault("ActionNotSupported", $"Path not handled: {path}");

                var bytes = System.Text.Encoding.UTF8.GetBytes(responseXml);
                resp.ContentType    = "application/soap+xml; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                await resp.OutputStream.WriteAsync(bytes);
                resp.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MultiChannel ONVIF request error.");
                try { ctx.Response.Close(); } catch { }
            }
        }

        // ── Device Service ────────────────────────────────────────────────

        private string HandleDeviceService(string soap)
        {
            if (soap.Contains("GetCapabilities"))   return GetCapabilities();
            if (soap.Contains("GetDeviceInformation")) return GetDeviceInformation();
            if (soap.Contains("GetNetworkInterfaces")) return GetNetworkInterfaces();
            if (soap.Contains("GetServices"))       return GetServices();
            if (soap.Contains("GetScopes"))         return GetScopes();
            return SoapFault("ActionNotSupported", "Device action not implemented.");
        }

        private string GetDeviceInformation() => SoapEnvelope("""
<tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
  <tds:Manufacturer>VisionCore</tds:Manufacturer>
  <tds:Model>MultiChannel Virtual Camera</tds:Model>
  <tds:FirmwareVersion>3.0</tds:FirmwareVersion>
  <tds:SerialNumber>VC-MC-0001</tds:SerialNumber>
  <tds:HardwareId>1.0</tds:HardwareId>
</tds:GetDeviceInformationResponse>
""");

        private string GetCapabilities() => SoapEnvelope($"""
<tds:GetCapabilitiesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                              xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:Capabilities>
    <tt:Media>
      <tt:XAddr>http://{WsDiscoveryResponder.GetLocalIp()}:{_port}/onvif/media_service</tt:XAddr>
    </tt:Media>
    <tt:Events>
      <tt:XAddr>http://{WsDiscoveryResponder.GetLocalIp()}:{_port}/onvif/events_service</tt:XAddr>
    </tt:Events>
  </tds:Capabilities>
</tds:GetCapabilitiesResponse>
""");

        private string GetServices() => SoapEnvelope($"""
<tds:GetServicesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
  <tds:Service>
    <tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace>
    <tds:XAddr>http://{WsDiscoveryResponder.GetLocalIp()}:{_port}/onvif/media_service</tds:XAddr>
    <tds:Version><tt:Major xmlns:tt="http://www.onvif.org/ver10/schema">2</tt:Major><tt:Minor xmlns:tt="http://www.onvif.org/ver10/schema">40</tt:Minor></tds:Version>
  </tds:Service>
</tds:GetServicesResponse>
""");

        private string GetScopes()
        {
            var channelNames = string.Join("\n", _channels.Values.Select(c =>
                $"  <tds:Scopes><tds:ScopeDef>Fixed</tds:ScopeDef>" +
                $"<tds:ScopeItem>onvif://www.onvif.org/name/{Uri.EscapeDataString(c.Name)}</tds:ScopeItem></tds:Scopes>"));
            return SoapEnvelope($"""
<tds:GetScopesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
  <tds:Scopes><tds:ScopeDef>Fixed</tds:ScopeDef><tds:ScopeItem>onvif://www.onvif.org/type/video_encoder</tds:ScopeItem></tds:Scopes>
  {channelNames}
</tds:GetScopesResponse>
""");
        }

        private string GetNetworkInterfaces() => SoapEnvelope("""
<tds:GetNetworkInterfacesResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl"
                                   xmlns:tt="http://www.onvif.org/ver10/schema">
  <tds:NetworkInterfaces token="eth0"><tt:Enabled>true</tt:Enabled></tds:NetworkInterfaces>
</tds:GetNetworkInterfacesResponse>
""");

        // ── Media Service — multi-profile ─────────────────────────────────

        private string HandleMediaService(string soap)
        {
            if (soap.Contains("GetProfiles"))    return GetProfiles();
            if (soap.Contains("GetProfile"))     return GetProfile(soap);
            if (soap.Contains("GetStreamUri"))   return GetStreamUri(soap);
            if (soap.Contains("GetVideoSources"))return GetVideoSources();
            if (soap.Contains("GetSnapshotUri")) return GetSnapshotUri(soap);
            return SoapFault("ActionNotSupported", "Media action not implemented.");
        }

        private string GetProfiles()
        {
            var profiles = string.Join("\n", _channels.Values.Select(BuildProfile));
            return SoapEnvelope($"""
<trt:GetProfilesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                          xmlns:tt="http://www.onvif.org/ver10/schema">
  {profiles}
</trt:GetProfilesResponse>
""");
        }

        private string GetProfile(string soap)
        {
            // Extract ProfileToken from request and return matching profile
            var token = ExtractXmlValue(soap, "ProfileToken");
            var cam   = _channels.Values.FirstOrDefault(c => $"profile_{c.Id:N}" == token)
                        ?? _channels.Values.FirstOrDefault();
            if (cam == null) return SoapFault("InvalidToken", "No channels registered.");
            return SoapEnvelope($"""
<trt:GetProfileResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                         xmlns:tt="http://www.onvif.org/ver10/schema">
  {BuildProfile(cam)}
</trt:GetProfileResponse>
""");
        }

        private static string BuildProfile(CameraConfig c)
        {
            var (w, h) = ResolutionPixels(c.Resolution);
            var codec  = c.Codec == VideoCodec.H265 ? "H265" : "H264";
            var token  = $"profile_{c.Id:N}";
            var vsc    = $"vsc_{c.Id:N}";
            var vec    = $"vec_{c.Id:N}";
            var vst    = $"vst_{c.Id:N}";
            return $"""
  <trt:Profiles token="{token}" fixed="true">
    <tt:Name>{System.Security.SecurityElement.Escape(c.Name)}</tt:Name>
    <tt:VideoSourceConfiguration token="{vsc}">
      <tt:Name>VideoSource-{System.Security.SecurityElement.Escape(c.Name)}</tt:Name>
      <tt:UseCount>1</tt:UseCount>
      <tt:SourceToken>{vst}</tt:SourceToken>
      <tt:Bounds x="0" y="0" width="{w}" height="{h}"/>
    </tt:VideoSourceConfiguration>
    <tt:VideoEncoderConfiguration token="{vec}">
      <tt:Name>VideoEncoder-{System.Security.SecurityElement.Escape(c.Name)}</tt:Name>
      <tt:UseCount>1</tt:UseCount>
      <tt:Encoding>{codec}</tt:Encoding>
      <tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution>
      <tt:Quality>5</tt:Quality>
      <tt:RateControl>
        <tt:FrameRateLimit>{c.FrameRate}</tt:FrameRateLimit>
        <tt:EncodingInterval>1</tt:EncodingInterval>
        <tt:BitrateLimit>{c.Bitrate}</tt:BitrateLimit>
      </tt:RateControl>
      <tt:H264><tt:GovLength>{c.FrameRate * 2}</tt:GovLength><tt:H264Profile>High</tt:H264Profile></tt:H264>
      <tt:SessionTimeout>PT60S</tt:SessionTimeout>
    </tt:VideoEncoderConfiguration>
  </trt:Profiles>
""";
        }

        private string GetVideoSources()
        {
            var sources = string.Join("\n", _channels.Values.Select(c =>
            {
                var (w, h) = ResolutionPixels(c.Resolution);
                return $"""
  <trt:VideoSources token="vst_{c.Id:N}">
    <tt:Framerate>{c.FrameRate}</tt:Framerate>
    <tt:Resolution><tt:Width>{w}</tt:Width><tt:Height>{h}</tt:Height></tt:Resolution>
  </trt:VideoSources>
""";
            }));
            return SoapEnvelope($"""
<trt:GetVideoSourcesResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                              xmlns:tt="http://www.onvif.org/ver10/schema">
  {sources}
</trt:GetVideoSourcesResponse>
""");
        }

        private string GetStreamUri(string soap)
        {
            var token = ExtractXmlValue(soap, "ProfileToken");
            var cam   = _channels.Values.FirstOrDefault(c => $"profile_{c.Id:N}" == token)
                        ?? _channels.Values.FirstOrDefault();
            if (cam == null) return SoapFault("InvalidToken", "No channels registered.");
            var rtspUrl = RtspStreamManager.GetClientUrl(cam);
            return SoapEnvelope($"""
<trt:GetStreamUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                           xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:MediaUri>
    <tt:Uri>{rtspUrl}</tt:Uri>
    <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
    <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
    <tt:Timeout>PT60S</tt:Timeout>
  </trt:MediaUri>
</trt:GetStreamUriResponse>
""");
        }

        private string GetSnapshotUri(string soap)
        {
            var token = ExtractXmlValue(soap, "ProfileToken");
            var cam   = _channels.Values.FirstOrDefault(c => $"profile_{c.Id:N}" == token)
                        ?? _channels.Values.FirstOrDefault();
            var id    = cam?.Id ?? Guid.Empty;
            var ip    = WsDiscoveryResponder.GetLocalIp();
            return SoapEnvelope($"""
<trt:GetSnapshotUriResponse xmlns:trt="http://www.onvif.org/ver10/media/wsdl"
                             xmlns:tt="http://www.onvif.org/ver10/schema">
  <trt:MediaUri>
    <tt:Uri>http://{ip}:{_port}/snapshot/{id}</tt:Uri>
    <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
    <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
    <tt:Timeout>PT60S</tt:Timeout>
  </trt:MediaUri>
</trt:GetSnapshotUriResponse>
""");
        }

        // ── Events Service ────────────────────────────────────────────────

        private string HandleEventsService(string soap)
        {
            if (soap.Contains("CreatePullPointSubscription")) return CreatePullPointSubscription();
            if (soap.Contains("PullMessages"))                return PullMessages();
            if (soap.Contains("Renew"))                       return RenewSubscription();
            if (soap.Contains("Unsubscribe"))                 return UnsubscribeResponse();
            return SoapFault("ActionNotSupported", "Event action not implemented.");
        }

        private string CreatePullPointSubscription() => SoapEnvelope($"""
<tev:CreatePullPointSubscriptionResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                                          xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
  <tev:SubscriptionReference>
    <wsnt:Address>http://{WsDiscoveryResponder.GetLocalIp()}:{_port}/onvif/events_service</wsnt:Address>
  </tev:SubscriptionReference>
  <tev:CurrentTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</tev:CurrentTime>
  <tev:TerminationTime>{DateTime.UtcNow.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}</tev:TerminationTime>
</tev:CreatePullPointSubscriptionResponse>
""");

        private string PullMessages()
        {
            var now = DateTime.UtcNow;
            var events = string.Join("\n", _motion
                .Where(kv => (now - kv.Value.lastSet).TotalSeconds < 5)
                .Select(kv =>
                {
                    var val = kv.Value.active ? "true" : "false";
                    return $"""
  <wsnt:NotificationMessage>
    <wsnt:Topic Dialect="http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet">
      tns1:RuleEngine/MotionRegionDetector/Motion
    </wsnt:Topic>
    <wsnt:Message>
      <tt:Message UtcTime="{now:yyyy-MM-ddTHH:mm:ssZ}" PropertyOperation="Changed"
                  xmlns:tt="http://www.onvif.org/ver10/schema">
        <tt:Source><tt:SimpleItem Name="VideoSource" Value="vst_{kv.Key:N}"/></tt:Source>
        <tt:Data><tt:SimpleItem Name="IsMotion" Value="{val}"/></tt:Data>
      </tt:Message>
    </wsnt:Message>
  </wsnt:NotificationMessage>
""";
                }));

            return SoapEnvelope($"""
<tev:PullMessagesResponse xmlns:tev="http://www.onvif.org/ver10/events/wsdl"
                           xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
  <tev:CurrentTime>{now:yyyy-MM-ddTHH:mm:ssZ}</tev:CurrentTime>
  <tev:TerminationTime>{now.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}</tev:TerminationTime>
  {events}
</tev:PullMessagesResponse>
""");
        }

        private string RenewSubscription() => SoapEnvelope($"""
<wsnt:RenewResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
  <wsnt:TerminationTime>{DateTime.UtcNow.AddHours(1):yyyy-MM-ddTHH:mm:ssZ}</wsnt:TerminationTime>
  <wsnt:CurrentTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</wsnt:CurrentTime>
</wsnt:RenewResponse>
""");

        private string UnsubscribeResponse() => SoapEnvelope("""
<wsnt:UnsubscribeResponse xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2"/>
""");

        // ── Helpers ───────────────────────────────────────────────────────

        private static string SoapEnvelope(string body) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
  <s:Body>{body}</s:Body>
</s:Envelope>
""";

        private static string SoapFault(string code, string reason) => SoapEnvelope($"""
<s:Fault>
  <s:Code><s:Value>s:{code}</s:Value></s:Code>
  <s:Reason><s:Text xml:lang="en">{System.Security.SecurityElement.Escape(reason)}</s:Text></s:Reason>
</s:Fault>
""");

        private static string ExtractXmlValue(string xml, string tag)
        {
            var open  = $"<{tag}>";
            var close = $"</{tag}>";
            var si    = xml.IndexOf(open,  StringComparison.OrdinalIgnoreCase);
            if (si < 0) return string.Empty;
            si += open.Length;
            var ei = xml.IndexOf(close, si, StringComparison.OrdinalIgnoreCase);
            return ei < 0 ? string.Empty : xml[si..ei].Trim();
        }

        private static (int w, int h) ResolutionPixels(Resolution r) => r switch
        {
            Resolution.R360p  => ( 640,  360),
            Resolution.R480p  => ( 854,  480),
            Resolution.R720p  => (1280,  720),
            Resolution.R4K    => (3840, 2160),
            _                 => (1920, 1080),
        };
    }

    // ── Stream extension helper ───────────────────────────────────────────────
    internal static class StreamExtensions
    {
        public static async Task<byte[]> ReadAllBytesAsync(
            this System.IO.Stream stream, CancellationToken ct)
        {
            using var ms = new System.IO.MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
    }
}
