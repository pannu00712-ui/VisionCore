using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Open.Nat;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Current state of the UPnP port-mapping attempt, surfaced to the
    /// Settings page so the user always knows what (if anything) has been
    /// exposed to the internet via their router.
    /// </summary>
    public enum UpnpStatus
    {
        /// <summary><see cref="AppSettings.EnableUpnp"/> is false — no action taken.</summary>
        Disabled,

        /// <summary>Not yet attempted (service starting, or never run).</summary>
        NotAttempted,

        /// <summary>Attempting to discover the router / create mappings.</summary>
        InProgress,

        /// <summary>All requested port mappings were created successfully.</summary>
        Success,

        /// <summary>Discovery or mapping failed — see <see cref="UpnpPortMappingService.StatusMessage"/>.</summary>
        Failed,
    }

    /// <summary>
    /// Opens (and later removes) UPnP IGD port mappings for VisionCore's
    /// network-facing services: RTSP, the REST API, and the configured ONVIF
    /// port range.
    ///
    /// Controlled by <see cref="AppSettings.EnableUpnp"/> (default false — this
    /// is opt-in because it exposes the host to the internet). The resulting
    /// <see cref="Status"/> / <see cref="StatusMessage"/> are read by
    /// SettingsViewModel and shown in the Settings page regardless of outcome.
    ///
    /// Uses the <c>Open.NAT</c> NuGet package (2.1.0) for UPnP IGD discovery
    /// and mapping. PMP/NAT-PMP is not attempted — UPnP IGD only.
    /// </summary>
    public sealed class UpnpPortMappingService
    {
        private const string MappingDescriptionPrefix = "VisionCore";
        private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(4);

        private readonly ILogger<UpnpPortMappingService> _logger;
        private readonly SettingsService _settings;

        private NatDevice? _device;
        private readonly List<Mapping> _activeMappings = new();

        public UpnpPortMappingService(
            ILogger<UpnpPortMappingService> logger,
            SettingsService settings)
        {
            _logger   = logger;
            _settings = settings;
        }

        /// <summary>Current overall status — bound by SettingsViewModel.</summary>
        public UpnpStatus Status { get; private set; } = UpnpStatus.NotAttempted;

        /// <summary>Human-readable detail for <see cref="Status"/> (error text, or list of mapped ports).</summary>
        public string StatusMessage { get; private set; } = string.Empty;

        /// <summary>
        /// Raised whenever <see cref="Status"/> or <see cref="StatusMessage"/> changes,
        /// so the Settings page can refresh without polling.
        /// </summary>
        public event EventHandler? StatusChanged;

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Attempts to open all required port mappings via UPnP IGD.
        /// No-op (sets Status = Disabled) if <see cref="AppSettings.EnableUpnp"/> is false.
        /// Never throws — failures are reported via <see cref="Status"/> / <see cref="StatusMessage"/>
        /// so a router without UPnP support does not prevent VisionCore from starting.
        /// </summary>
        public async Task StartAsync()
        {
            var app = _settings.App;

            if (!app.EnableUpnp)
            {
                SetStatus(UpnpStatus.Disabled, "UPnP port mapping is disabled in Settings.");
                return;
            }

            SetStatus(UpnpStatus.InProgress, "Discovering UPnP-capable router…");

            try
            {
                var discoverer = new NatDiscoverer();
                using var cts = new System.Threading.CancellationTokenSource(DiscoveryTimeout);
                _device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);

                var ports = BuildRequestedPorts(app);
                var created = new List<string>();
                var failed  = new List<string>();

                foreach (var (port, label) in ports)
                {
                    try
                    {
                        var mapping = new Mapping(
                            Protocol.Tcp,
                            privatePort: port,
                            publicPort: port,
                            lifetime: 0, // 0 = permanent (router-dependent; renewed on each service start anyway)
                            description: $"{MappingDescriptionPrefix} {label} ({port})");

                        await _device.CreatePortMapAsync(mapping);
                        _activeMappings.Add(mapping);
                        created.Add($"{label} {port}/tcp");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "UPnP: failed to map port {Port} ({Label}).", port, label);
                        failed.Add($"{label} {port}/tcp");
                    }
                }

                if (created.Count == 0)
                {
                    SetStatus(UpnpStatus.Failed,
                        "Router found but no port mappings could be created. " +
                        "Check router UPnP settings and firewall rules.");
                    return;
                }

                var externalIp = await TryGetExternalIpAsync();
                var msg = $"Mapped: {string.Join(", ", created)}" +
                          (externalIp != null ? $" — external IP {externalIp}" : "");

                if (failed.Count > 0)
                    msg += $". Failed: {string.Join(", ", failed)}";

                SetStatus(failed.Count == 0 ? UpnpStatus.Success : UpnpStatus.Failed, msg);
            }
            catch (NatDeviceNotFoundException)
            {
                SetStatus(UpnpStatus.Failed,
                    "No UPnP-capable router found on the network. " +
                    "Port forwarding must be configured manually if remote access is required.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "UPnP discovery/mapping failed.");
                SetStatus(UpnpStatus.Failed, $"UPnP setup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes any port mappings created by <see cref="StartAsync"/>.
        /// Safe to call even if <see cref="StartAsync"/> was never called or failed.
        /// </summary>
        public async Task StopAsync()
        {
            if (_device == null || _activeMappings.Count == 0)
                return;

            foreach (var mapping in _activeMappings)
            {
                try
                {
                    await _device.DeletePortMapAsync(mapping);
                    _logger.LogInformation("UPnP: removed port mapping {Description}.", mapping.Description);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "UPnP: failed to remove port mapping {Description}.", mapping.Description);
                }
            }

            _activeMappings.Clear();
            _device = null;
            SetStatus(UpnpStatus.NotAttempted, "Port mappings removed.");
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the list of (port, label) pairs to map: RTSP, REST API, and
        /// every distinct per-camera ONVIF port (typically the 8080–8090 range),
        /// plus the MultipleChannel shared ONVIF port if configured.
        /// </summary>
        private static List<(int port, string label)> BuildRequestedPorts(AppSettings app)
        {
            var ports = new List<(int port, string label)>
            {
                (app.RtspPort, "RTSP"),
            };

            if (app.RestApiEnabled)
                ports.Add((app.RestApiPort, "REST API"));

            if (app.OnvifMode == OnvifMode.MultipleChannel)
            {
                ports.Add((app.OnvifSharedPort, "ONVIF"));
            }
            else
            {
                // MultipleCamera mode: per-camera ONVIF ports aren't known to
                // AppSettings directly, so map the conventional 8080-8090
                // range used by CameraConfig.OnvifPort defaults.
                for (var p = 8080; p <= 8090; p++)
                    ports.Add((p, "ONVIF"));
            }

            return ports;
        }

        private async Task<string?> TryGetExternalIpAsync()
        {
            try
            {
                if (_device == null) return null;
                var ip = await _device.GetExternalIPAsync();
                return ip?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private void SetStatus(UpnpStatus status, string message)
        {
            Status        = status;
            StatusMessage = message;
            _logger.LogInformation("UPnP status: {Status} — {Message}", status, message);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
