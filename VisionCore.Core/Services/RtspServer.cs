using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VisionCore.Core.Interfaces;
using VisionCore.Core.Models;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// RTSP Server — Hybrid approach:
    ///   1. Spawns MediaMTX (mediamtx.exe) as a child process with a generated config
    ///   2. Manages per-stream path registration with username/password auth
    ///   3. Exposes MediaMTX HTTP API (port 9997) for client count / stats
    ///   4. Falls back gracefully if MediaMTX binary is not found
    ///
    /// MediaMTX config is written to %Temp%\VisionCore\mediamtx.yml on each start.
    /// Binary expected at: {AppDir}\mediamtx\mediamtx.exe
    /// Download: https://github.com/bluenviron/mediamtx/releases
    /// </summary>
    public sealed class RtspServer : IRtspServer, IDisposable
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private const int ApiPort         = 9997;   // MediaMTX HTTP API
        private const int DefaultRtspPort = 8554;
        private const int DefaultRtmpPort = 1935;
        private const int DefaultHlsPort  = 8888;

        // ── State ─────────────────────────────────────────────────────────────
        private readonly ILogger<RtspServer> _logger;
        private readonly HttpClient _http;
        private Process? _mediaMtx;
        private CancellationTokenSource? _cts;
        private string? _configPath;

        /// path → StreamRegistration
        private readonly ConcurrentDictionary<string, StreamRegistration> _streams = new();

        public bool IsListening { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────

        public RtspServer(ILogger<RtspServer> logger)
        {
            _logger = logger;
            _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        }

        // ── IRtspServer ───────────────────────────────────────────────────────

        public async Task StartAsync(int port, CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var binary = FindMediaMtx();
            if (binary == null)
            {
                _logger.LogWarning(
                    "mediamtx.exe not found. RTSP server disabled. " +
                    "Place mediamtx.exe in {Dir}\\mediamtx\\",
                    AppContext.BaseDirectory);
                return;
            }

            _configPath = WriteConfig();

            var psi = new ProcessStartInfo
            {
                FileName               = binary,
                Arguments              = $"\"{_configPath}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            _mediaMtx = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _mediaMtx.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger.LogDebug("[MediaMTX] {Line}", e.Data);
            };
            _mediaMtx.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger.LogWarning("[MediaMTX] {Line}", e.Data);
            };
            _mediaMtx.Exited += (_, _) =>
            {
                if (IsListening)
                    _logger.LogWarning("MediaMTX process exited unexpectedly — attempting restart.");
                IsListening = false;
            };

            _mediaMtx.Start();
            _mediaMtx.BeginOutputReadLine();
            _mediaMtx.BeginErrorReadLine();

            // Wait until API becomes responsive (max 5 s)
            await WaitForApiAsync(_cts.Token);
            IsListening = true;

            _logger.LogInformation(
                "MediaMTX started (PID {Pid}). RTSP on :{RtspPort}, API on :{ApiPort}",
                _mediaMtx.Id, DefaultRtspPort, ApiPort);
        }

        public async Task StopAsync()
        {
            IsListening = false;
            _cts?.Cancel();

            if (_mediaMtx != null && !_mediaMtx.HasExited)
            {
                try
                {
                    _mediaMtx.Kill(entireProcessTree: true);
                    await Task.Run(() => _mediaMtx.WaitForExit(4000));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error killing MediaMTX process.");
                }
            }

            if (_configPath != null && File.Exists(_configPath))
            {
                try { File.Delete(_configPath); } catch { /* best effort */ }
            }

            _logger.LogInformation("RTSP server stopped.");
        }

        /// <summary>
        /// Register a new virtual camera stream path in MediaMTX via its HTTP API.
        /// Creates the path with optional credentials and publisher allow-list.
        /// </summary>
        public void RegisterStream(Guid cameraId, string path, IFrameSource source)
        {
            var reg = new StreamRegistration(cameraId, path, source);
            _streams[path] = reg;

            // Tell MediaMTX about this path dynamically
            _ = AddPathAsync(path, reg.Username, reg.Password);

            _logger.LogInformation(
                "Stream registered: rtsp://localhost:{Port}/{Path}", DefaultRtspPort, path);
        }

        public void UnregisterStream(Guid cameraId)
        {
            foreach (var kvp in _streams)
            {
                if (kvp.Value.CameraId == cameraId)
                {
                    _streams.TryRemove(kvp.Key, out _);
                    _ = RemovePathAsync(kvp.Key);
                    _logger.LogInformation("Stream unregistered: {Path}", kvp.Key);
                    break;
                }
            }
        }

        public int GetClientCount(Guid cameraId)
        {
            foreach (var reg in _streams.Values)
            {
                if (reg.CameraId == cameraId)
                    return QueryClientCountAsync(reg.Path).GetAwaiter().GetResult();
            }
            return 0;
        }

        /// <summary>
        /// Returns the cumulative bytes sent to readers for this camera's path,
        /// as reported by MediaMTX's <c>/v3/paths/list</c> API. Returns 0 if the
        /// path isn't found or MediaMTX isn't reachable.
        /// </summary>
        public long GetBytesSent(Guid cameraId)
        {
            foreach (var reg in _streams.Values)
            {
                if (reg.CameraId == cameraId)
                    return QueryPathBytesSentAsync(reg.Path).GetAwaiter().GetResult();
            }
            return 0;
        }

        /// <summary>
        /// Queries MediaMTX <c>/v3/paths/list</c> and returns the
        /// <c>bytesSent</c> counter for the given path (0 if not found).
        /// </summary>
        private async Task<long> QueryPathBytesSentAsync(string path)
        {
            if (!IsListening) return 0;
            try
            {
                var url  = $"http://127.0.0.1:{ApiPort}/v3/paths/list";
                var resp = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(resp);

                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var n) && n.GetString() == path &&
                            item.TryGetProperty("bytesSent", out var bs))
                        {
                            return bs.GetInt64();
                        }
                    }
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        // ── MediaMTX Config generation ────────────────────────────────────────

        /// <summary>
        /// Generates a complete mediamtx.yml that:
        ///   - Enables RTSP on 8554
        ///   - Enables RTMP on 1935 (optional — useful for OBS)
        ///   - Enables HLS on 8888
        ///   - Enables HTTP API on 9997
        ///   - Sets paths with per-stream auth (patched dynamically via API later)
        /// </summary>
        private string WriteConfig()
        {
            var dir = Path.Combine(Path.GetTempPath(), "VisionCore");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mediamtx.yml");

            // Build initial path entries for already-registered streams
            var pathsYaml = new StringBuilder();
            foreach (var reg in _streams.Values)
            {
                pathsYaml.AppendLine($"  {reg.Path}:");
                if (!string.IsNullOrEmpty(reg.Username))
                {
                    pathsYaml.AppendLine($"    readUser: {reg.Username}");
                    pathsYaml.AppendLine($"    readPass: {reg.Password}");
                }
                pathsYaml.AppendLine($"    source: publisher");
            }

            var yaml = $"""
# VisionCore — auto-generated MediaMTX config
# Do not edit manually; changes will be overwritten on restart.

###############################################################
# Logging
###############################################################
logLevel: warn
logDestinations: [stdout]

###############################################################
# RTSP
###############################################################
rtsp: yes
rtspAddress: :8554
protocols: [tcp, udp]
encryption: "no"

###############################################################
# RTMP  (useful for OBS, Streamlabs, etc.)
###############################################################
rtmp: yes
rtmpAddress: :1935

###############################################################
# HLS  (browser playback via HTTP)
###############################################################
hls: yes
hlsAddress: :8888
hlsAlwaysRemux: yes
hlsSegmentCount: 3
hlsSegmentDuration: 1s

###############################################################
# HTTP API  (used by VisionCore for stats & path management)
###############################################################
api: yes
apiAddress: :{ApiPort}

###############################################################
# Metrics (Prometheus-compatible)
###############################################################
metrics: no

###############################################################
# Path defaults
###############################################################
pathDefaults:
  source: publisher
  sourceOnDemand: no
  readUser: ""
  readPass: ""
  publishUser: ""
  publishPass: ""

###############################################################
# Registered paths
###############################################################
paths:
{pathsYaml}
""";

            File.WriteAllText(path, yaml, Encoding.UTF8);
            _logger.LogDebug("MediaMTX config written to {Path}", path);
            return path;
        }

        // ── MediaMTX HTTP API helpers ─────────────────────────────────────────

        private async Task AddPathAsync(string path, string? user, string? pass)
        {
            if (!IsListening) return;
            try
            {
                var body = new Dictionary<string, object?> { ["source"] = "publisher" };
                if (!string.IsNullOrEmpty(user))
                {
                    body["readUser"] = user;
                    body["readPass"] = pass;
                }

                var json    = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var url     = $"http://127.0.0.1:{ApiPort}/v3/config/paths/add/{Uri.EscapeDataString(path)}";
                var resp    = await _http.PostAsync(url, content);

                if (resp.IsSuccessStatusCode)
                    _logger.LogDebug("Path '{Path}' added via API.", path);
                else
                    _logger.LogWarning("MediaMTX API: add path returned {Status}", resp.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add path '{Path}' via MediaMTX API.", path);
            }
        }

        private async Task RemovePathAsync(string path)
        {
            if (!IsListening) return;
            try
            {
                var url  = $"http://127.0.0.1:{ApiPort}/v3/config/paths/delete/{Uri.EscapeDataString(path)}";
                await _http.DeleteAsync(url);
            }
            catch { /* ignore — process may already be gone */ }
        }

        private async Task<int> QueryClientCountAsync(string path)
        {
            if (!IsListening) return 0;
            try
            {
                var url  = $"http://127.0.0.1:{ApiPort}/v3/rtspconns/list";
                var resp = await _http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(resp);

                int count = 0;
                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("path", out var p) &&
                            p.GetString() == path)
                            count++;
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Polls MediaMTX HTTP API until it responds (or timeout).
        /// </summary>
        private async Task WaitForApiAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    var resp = await _http.GetAsync(
                        $"http://127.0.0.1:{ApiPort}/v3/paths/list", ct);
                    if (resp.IsSuccessStatusCode) return;
                }
                catch { /* not ready yet */ }
                await Task.Delay(400, ct);
            }
            _logger.LogWarning("MediaMTX API did not respond within 10 s.");
        }

        // ── MediaMTX binary discovery ─────────────────────────────────────────

        private static string? FindMediaMtx()
        {
            // 1. Bundled alongside the app
            var bundled = Path.Combine(AppContext.BaseDirectory, "mediamtx", "mediamtx.exe");
            if (File.Exists(bundled)) return bundled;

            // 2. Same directory as the app
            var sibling = Path.Combine(AppContext.BaseDirectory, "mediamtx.exe");
            if (File.Exists(sibling)) return sibling;

            // 3. System PATH
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                var candidate = Path.Combine(dir.Trim(), "mediamtx.exe");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _http.Dispose();
            _mediaMtx?.Dispose();
            _cts?.Dispose();
        }
    }

    // ── Supporting types ──────────────────────────────────────────────────────

    /// <summary>Metadata for a registered virtual camera stream.</summary>
    internal sealed class StreamRegistration
    {
        public Guid         CameraId  { get; }
        public string       Path      { get; }
        public IFrameSource Source    { get; }
        public string?      Username  { get; }
        public string?      Password  { get; }

        public StreamRegistration(Guid id, string path, IFrameSource src,
                                  string? user = null, string? pass = null)
        {
            CameraId = id;
            Path     = path;
            Source   = src;
            Username = user;
            Password = pass;
        }
    }
}
