using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VisionCore.Core.Models;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Bridges the gap between FFmpegEngine and MediaMTX:
    ///
    ///   FFmpegEngine  ──(local RTSP push)──▶  MediaMTX  ──▶  ONVIF / VLC / NVR clients
    ///
    /// How it works:
    ///   FFmpeg captures screen/webcam and pushes to MediaMTX via
    ///   rtsp://127.0.0.1:8554/{path}  (RTSP publish / "source: publisher" mode).
    ///   MediaMTX re-distributes to all connecting clients — no extra relay needed.
    ///
    /// Authentication:
    ///   - Publisher side  → FFmpeg pushes with publishUser / publishPass
    ///   - Reader  side    → client connects with readUser / readPass
    ///   Both sets of credentials are generated per-camera and stored in CameraConfig.
    ///
    /// URL format exposed to clients:
    ///   rtsp://[readUser]:[readPass]@[host]:8554/[path]
    /// </summary>
    public sealed class RtspStreamManager : IDisposable
    {
        private readonly ILogger<RtspStreamManager> _logger;
        private readonly RtspServer _rtspServer;

        // cameraId → FFmpeg publisher process
        private readonly ConcurrentDictionary<Guid, Process> _publishers = new();
        private readonly ConcurrentDictionary<Guid, CameraStats> _statsMap   = new();
        private readonly ConcurrentDictionary<Guid, CameraConfig> _configs    = new();

        // cameraId → (last CPU sample time, last TotalProcessorTime)
        private readonly ConcurrentDictionary<Guid, (DateTime SampledAt, TimeSpan CpuTime)> _cpuSamples = new();

        // cameraId → (last sample time, last bytesSent) for bitrate calculation
        private readonly ConcurrentDictionary<Guid, (DateTime SampledAt, long BytesSent)> _bitrateSamples = new();

        private static readonly TimeSpan StatsRefreshInterval = TimeSpan.FromSeconds(2);
        private System.Threading.Timer? _statsTimer;

        public RtspStreamManager(ILogger<RtspStreamManager> logger, RtspServer rtspServer)
        {
            _logger     = logger;
            _rtspServer = rtspServer;

            _statsTimer = new System.Threading.Timer(
                _ => RefreshAllStats(),
                null,
                StatsRefreshInterval,
                StatsRefreshInterval);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Start publishing a camera's stream into MediaMTX.
        /// This is called automatically by CameraManager after the RTSP server is ready.
        /// </summary>
        public async Task StartPublisherAsync(CameraConfig config, CancellationToken ct = default)
        {
            if (_publishers.ContainsKey(config.Id))
            {
                _logger.LogWarning("Publisher for camera {Id} already running.", config.Id);
                return;
            }

            // Ensure credentials exist
            EnsureCredentials(config);

            var args = BuildPublisherArgs(config);
            _logger.LogDebug("[Publisher/{Name}] FFmpeg args: {Args}", config.Name, args);

            var psi = new ProcessStartInfo
            {
                FileName               = FindFfmpeg(),
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardError  = true,
                StandardErrorEncoding  = Encoding.UTF8,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger.LogDebug("[FFmpeg/{Name}] {Line}", config.Name, e.Data);
            };
            proc.Exited += (_, _) =>
            {
                _publishers.TryRemove(config.Id, out _);
                if (!ct.IsCancellationRequested)
                    _logger.LogWarning("FFmpeg publisher for '{Name}' exited.", config.Name);
            };

            proc.Start();
            proc.BeginErrorReadLine();
            _publishers[config.Id] = proc;
            _configs[config.Id]    = config;
            _statsMap[config.Id]   = new CameraStats { CameraId = config.Id, IsRunning = true, StartedAt = DateTime.UtcNow };

            // Seed CPU/bitrate baselines so the first tick doesn't report a huge spike
            try { _cpuSamples[config.Id] = (DateTime.UtcNow, proc.TotalProcessorTime); }
            catch { _cpuSamples[config.Id] = (DateTime.UtcNow, TimeSpan.Zero); }
            _bitrateSamples[config.Id] = (DateTime.UtcNow, 0);

            await Task.Delay(600, ct); // give FFmpeg a moment to connect
            _logger.LogInformation(
                "Publisher started for '{Name}' → rtsp://127.0.0.1:8554/{Path}",
                config.Name, config.RtspPath);
        }

        public async Task StopPublisherAsync(Guid cameraId)
        {
            if (!_publishers.TryRemove(cameraId, out var proc)) return;
            if (_statsMap.TryGetValue(cameraId, out var s))
            {
                s.IsRunning   = false;
                s.CpuUsage    = 0;
                s.CurrentBitrateKbps = 0;
            }
            _configs.TryRemove(cameraId, out _);
            _cpuSamples.TryRemove(cameraId, out _);
            _bitrateSamples.TryRemove(cameraId, out _);

            if (!proc.HasExited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    await Task.Run(() => proc.WaitForExit(3000));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping publisher process.");
                }
            }

            proc.Dispose();
        }

public async Task StopAllAsync()
        {
            foreach (var id in _publishers.Keys)
                await StopPublisherAsync(id);
        }

        /// <summary>Returns true if the FFmpeg publisher process is alive for a given camera.</summary>
        public bool IsPublisherRunning(Guid cameraId) =>
            _publishers.TryGetValue(cameraId, out var p) && !p.HasExited;


        /// <summary>Total RTSP clients connected across all running cameras.</summary>
        public int TotalConnections
        {
            get
            {
                int total = 0;
                foreach (var cfg in _statsMap.Values)
                    total += cfg.ActiveClients;
                return total;
            }
        }

        /// <summary>Stats snapshot for a single camera, or null if not tracked.</summary>
        public CameraStats? GetStats(Guid cameraId)
            => _statsMap.TryGetValue(cameraId, out var s) ? s : null;

        /// <summary>Stats for all cameras keyed by camera ID.</summary>
        public Dictionary<Guid, CameraStats> GetAllStats()
            => new Dictionary<Guid, CameraStats>(_statsMap);

        // ── Live stats refresh (CPU / bitrate / clients / uptime) ──────────────

        /// <summary>
        /// Called every <see cref="StatsRefreshInterval"/> (2 s) by <see cref="_statsTimer"/>.
        /// For each running publisher, updates:
        ///   • Uptime                  — wall-clock since StartedAt
        ///   • CpuUsage (%)            — delta of Process.TotalProcessorTime / wall-clock delta / CPU core count
        ///   • CurrentBitrateKbps      — delta of MediaMTX bytesSent / wall-clock delta
        ///   • BytesSent               — latest cumulative value from MediaMTX
        ///   • ActiveClients           — current RTSP reader count from MediaMTX
        ///   • GpuEncoder              — friendly label derived from CameraConfig.GpuAccel
        /// </summary>
        private void RefreshAllStats()
        {
            var now = DateTime.UtcNow;
            int cpuCount = Environment.ProcessorCount;

            foreach (var kvp in _publishers)
            {
                var cameraId = kvp.Key;
                var proc     = kvp.Value;

                if (!_statsMap.TryGetValue(cameraId, out var stats)) continue;

                try
                {
                    if (proc.HasExited)
                    {
                        stats.IsRunning      = false;
                        stats.CpuUsage       = 0;
                        stats.CurrentBitrateKbps = 0;
                        continue;
                    }

                    stats.IsRunning = true;
                    stats.Uptime    = now - stats.StartedAt;

                    // ── CPU usage % ──────────────────────────────────────────
                    proc.Refresh();
                    var currentCpuTime = proc.TotalProcessorTime;
                    if (_cpuSamples.TryGetValue(cameraId, out var lastCpu))
                    {
                        var wallDelta = (now - lastCpu.SampledAt).TotalMilliseconds;
                        var cpuDelta  = (currentCpuTime - lastCpu.CpuTime).TotalMilliseconds;
                        if (wallDelta > 0)
                        {
                            var pct = (cpuDelta / wallDelta) / cpuCount * 100.0;
                            stats.CpuUsage = Math.Max(0, Math.Round(pct, 1));
                        }
                    }
                    _cpuSamples[cameraId] = (now, currentCpuTime);

                    // ── Bitrate (kbps) from MediaMTX bytesSent delta ────────
                    var bytesSent = _rtspServer.GetBytesSent(cameraId);
                    if (_bitrateSamples.TryGetValue(cameraId, out var lastBytes))
                    {
                        var wallDelta = (now - lastBytes.SampledAt).TotalSeconds;
                        var byteDelta = bytesSent - lastBytes.BytesSent;
                        if (wallDelta > 0 && byteDelta >= 0)
                        {
                            // bytes → bits → kbps
                            stats.CurrentBitrateKbps = Math.Round((byteDelta * 8.0 / 1000.0) / wallDelta, 1);
                        }
                    }
                    stats.BytesSent = bytesSent;
                    _bitrateSamples[cameraId] = (now, bytesSent);

                    // ── Active clients ───────────────────────────────────────
                    stats.ActiveClients = _rtspServer.GetClientCount(cameraId);

                    // ── GPU encoder label ────────────────────────────────────
                    if (_configs.TryGetValue(cameraId, out var cfg))
                        stats.GpuEncoder = GpuEncoderLabel(cfg.GpuAccel);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Stats refresh failed for camera {Id}.", cameraId);
                }
            }
        }

        /// <summary>
        /// Captures a single JPEG snapshot from a running camera's RTSP stream
        /// using a one-shot FFmpeg invocation (<c>-vframes 1</c>).
        /// Returns the JPEG bytes, or null if the camera isn't running or the
        /// capture failed/timed out.
        /// </summary>
        public async Task<byte[]?> CaptureSnapshotAsync(Guid cameraId, CancellationToken ct = default)
        {
            if (!_configs.TryGetValue(cameraId, out var config))
                return null;

            if (!IsPublisherRunning(cameraId))
                return null;

            var url = GetClientUrl(config, "127.0.0.1");
            var tempFile = Path.Combine(Path.GetTempPath(), $"visioncore_snap_{cameraId:N}.jpg");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName              = FindFfmpeg(),
                    Arguments             = $"-y -loglevel error -rtsp_transport tcp -i \"{url}\" " +
                                             $"-frames:v 1 -q:v 2 \"{tempFile}\"",
                    UseShellExecute       = false,
                    CreateNoWindow        = true,
                    RedirectStandardError = true,
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var completed = await Task.Run(() => proc.WaitForExit(8000), ct);
                if (!completed)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    _logger.LogWarning("Snapshot capture timed out for camera {Id}.", cameraId);
                    return null;
                }

                if (!File.Exists(tempFile))
                {
                    _logger.LogWarning("Snapshot capture produced no file for camera {Id}.", cameraId);
                    return null;
                }

                var bytes = await File.ReadAllBytesAsync(tempFile, ct);
                return bytes;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Snapshot capture failed for camera {Id}.", cameraId);
                return null;
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { /* ignore */ }
            }
        }


        private static string GpuEncoderLabel(GpuAccel accel) => accel switch
        {
            GpuAccel.NvencH264 => "NVENC H.264",
            GpuAccel.NvencH265 => "NVENC H.265",
            GpuAccel.QuickSync => "Quick Sync",
            GpuAccel.Amd       => "AMD AMF",
            _                  => "CPU",
        };

        /// <summary>
        /// Returns the full RTSP URL a client should use to connect (with read credentials).
        /// </summary>
        public static string GetClientUrl(CameraConfig config, string host = "127.0.0.1")
        {
            if (!string.IsNullOrEmpty(config.Username))
                return $"rtsp://{config.Username}:{config.Password}@{host}:8554/{config.RtspPath}";
            return $"rtsp://{host}:8554/{config.RtspPath}";
        }

        // ── Argument builder ──────────────────────────────────────────────────

        private string BuildPublisherArgs(CameraConfig config)
        {
            var sb = new StringBuilder();
            sb.Append("-y -loglevel warning -stats ");

            // ── Input ──────────────────────────────────────────────────────
            var (w, h) = ResolutionPixels(config.Resolution);
            var fps    = config.FrameRate;

            // Video encoder (computed up-front so the AudioOnly fast-path can use it too)
            var enc = config.GpuAccel switch
            {
                GpuAccel.NvencH264 => "h264_nvenc",
                GpuAccel.NvencH265 => "hevc_nvenc",
                GpuAccel.QuickSync => "h264_qsv",
                GpuAccel.Amd      => "h264_amf",
                _ => config.Codec == VideoCodec.H265 ? "libx265" : "libx264"
            };

            // ── Audio-only fast path ──────────────────────────────────────────
            // Generates a black 640x360 video frame with an audio-level visualizer
            // (FFmpeg lavfi color source + showvolume filter on the captured audio),
            // since AudioOnly streams still need a video track for ONVIF/RTSP clients.
            if (config.Source == CameraSource.AudioOnly)
            {
                var aoAudioIn = config.AudioSource switch
                {
                    AudioSource.DesktopAudio => "audio=\"virtual-audio-capturer\"",
                    _ => $"audio=\"{config.MicDeviceId ?? "Microphone"}\""
                };

                sb.Append($"-f lavfi -i color=c=black:s=640x360:r={fps} ");
                sb.Append($"-f dshow -i {aoAudioIn} ");
                sb.Append("-filter_complex \"[1:a]showvolume=w=640:h=80:f=2:c=Lime:rate=" + fps + "[vol];" +
                          "[0:v][vol]overlay=0:H-h-10[outv]\" ");
                sb.Append("-map \"[outv]\" -map 1:a ");

                sb.Append($"-c:v {enc} -b:v {config.Bitrate}k -maxrate {config.Bitrate * 2}k " +
                          $"-bufsize {config.Bitrate * 2}k -preset fast " +
                          $"-g {fps * 2} -r {fps} ");
                if (enc.Contains("nvenc")) sb.Append("-rc:v vbr_hq ");

                sb.Append($"-c:a aac -b:a {config.AudioBitrate}k -ar 44100 ");

                sb.Append("-f rtsp -rtsp_transport tcp ");
                if (!string.IsNullOrEmpty(config.Username))
                    sb.Append($"rtsp://publisher:{config.Password}@127.0.0.1:8554/{config.RtspPath}");
                else
                    sb.Append($"rtsp://127.0.0.1:8554/{config.RtspPath}");

                return sb.ToString().Trim();
            }

            switch (config.Source)
            {
                case CameraSource.Screen:
                    if (config.Region != null)
                    {
                        var r = config.Region;
                        sb.Append($"-f gdigrab -framerate {fps} " +
                                  $"-offset_x {r.X} -offset_y {r.Y} " +
                                  $"-video_size {r.Width}x{r.Height} -draw_mouse 1 -i desktop ");
                    }
                    else
                    {
                        sb.Append($"-f gdigrab -framerate {fps} -draw_mouse 1 -i desktop ");
                    }
                    break;

                case CameraSource.Webcam:
                    var webcam = config.WebcamDeviceId ?? "video=0";
                    sb.Append($"-f dshow -framerate {fps} -video_size {w}x{h} -i \"{webcam}\" ");
                    break;

                case CameraSource.Combined:
                    // Screen as primary input, webcam as secondary
                    sb.Append($"-f gdigrab -framerate {fps} -draw_mouse 1 -i desktop ");
                    var wcam = config.WebcamDeviceId ?? "video=0";
                    sb.Append($"-f dshow -framerate {fps} -i \"{wcam}\" ");
                    break;

                case CameraSource.ExternalRtsp:
                    // Re-publish an existing RTSP source through MediaMTX / ONVIF
                    var extRtsp = config.InputUrl ?? string.Empty;
                    sb.Append($"-i \"{extRtsp}\" ");
                    break;

                case CameraSource.ExternalHttp:
                    // Re-publish an HTTP/MJPEG source through MediaMTX / ONVIF
                    var extHttp = config.InputUrl ?? string.Empty;
                    sb.Append($"-i \"{extHttp}\" ");
                    break;

                case CameraSource.AppWindow:
                    // Capture a specific application window by partial title match via gdigrab.
                    // Falls back to full desktop if WindowTitle is not set.
                    var winTitle = string.IsNullOrWhiteSpace(config.WindowTitle)
                        ? "desktop"
                        : $"title={config.WindowTitle}";
                    sb.Append($"-f gdigrab -framerate {fps} -draw_mouse 0 -i \"{winTitle}\" ");
                    break;

                case CameraSource.StaticImage:
                    // Loop a single image file as a continuous video stream.
                    var imgPath = (config.StaticImagePath ?? string.Empty).Replace("\\", "/");
                    sb.Append($"-loop 1 -framerate {fps} -i \"{imgPath}\" ");
                    break;
            }

            // ── Audio ──────────────────────────────────────────────────────
            if (config.AudioSource != AudioSource.None)
            {
                var audioIn = config.AudioSource switch
                {
                    AudioSource.Microphone => $"audio=\"{config.MicDeviceId ?? "Microphone"}\"",
                    AudioSource.DesktopAudio     => "audio=\"virtual-audio-capturer\"",
                    // AudioSource.Both removed — use Microphone only
                    _ => ""
                };
                if (!string.IsNullOrEmpty(audioIn))
                    sb.Append($"-f dshow -i {audioIn} ");
            }

            // ── Filters ────────────────────────────────────────────────────
            sb.Append(BuildFilters(config, w, h));

            // ── Video encoder ──────────────────────────────────────────────
            sb.Append($"-c:v {enc} -b:v {config.Bitrate}k -maxrate {config.Bitrate * 2}k " +
                      $"-bufsize {config.Bitrate * 2}k -preset fast " +
                      $"-g {fps * 2} -r {fps} ");
            if (enc.Contains("nvenc")) sb.Append("-rc:v vbr_hq ");

            // ── Audio encoder ──────────────────────────────────────────────
            if (config.AudioSource != AudioSource.None)
                sb.Append($"-c:a aac -b:a {config.AudioBitrate}k -ar 44100 ");
            else
                sb.Append("-an ");

            // ── Output to MediaMTX ─────────────────────────────────────────
            sb.Append("-f rtsp -rtsp_transport tcp ");

            // Publisher credentials (if auth is required)
            if (!string.IsNullOrEmpty(config.Username))
            {
                // MediaMTX publisher auth via URL credentials
                sb.Append($"rtsp://publisher:{config.Password}@127.0.0.1:8554/{config.RtspPath}");
            }
            else
            {
                sb.Append($"rtsp://127.0.0.1:8554/{config.RtspPath}");
            }

            return sb.ToString().Trim();
        }

        private static string BuildFilters(CameraConfig config, int w, int h)
        {
            var sb = new StringBuilder();
            var parts = new System.Collections.Generic.List<string>();
            var lastLabel = "[0:v]";

            // Scale
            parts.Add($"{lastLabel}scale={w}:{h}:flags=lanczos[scaled]");
            lastLabel = "[scaled]";

            // Combined mode: PiP webcam overlay
            if (config.Source == CameraSource.Combined)
            {
                parts.Add($"{lastLabel}[1:v]overlay=W-w-20:H-h-20[pip]");
                lastLabel = "[pip]";
            }

            // Overlays
            foreach (var ov in config.Overlays)
            {
                var next = $"[ov{parts.Count}]";
                switch (ov.Type)
                {
                    case OverlayType.Timestamp:
                        parts.Add($"{lastLabel}drawtext=fontsize={ov.FontSize}:" +
                                  $"fontcolor={ov.Color}:text='%{{localtime}}':" +
                                  $"x={ov.X}:y={ov.Y}{next}");
                        lastLabel = next;
                        break;
                    case OverlayType.Text when !string.IsNullOrEmpty(ov.Text):
                        var safe = ov.Text.Replace("'", "\\'");
                        parts.Add($"{lastLabel}drawtext=fontsize={ov.FontSize}:" +
                                  $"fontcolor={ov.Color}:text='{safe}':" +
                                  $"x={ov.X}:y={ov.Y}{next}");
                        lastLabel = next;
                        break;
                    case OverlayType.Logo when !string.IsNullOrEmpty(ov.LogoPath):
                        var lp = ov.LogoPath.Replace("\\", "/");
                        parts.Add($"movie='{lp}'[logo{parts.Count}]; " +
                                  $"{lastLabel}[logo{parts.Count}]overlay={ov.X}:{ov.Y}{next}");
                        lastLabel = next;
                        break;

                    case OverlayType.CameraName:
                        var cn = (config.Name ?? "Camera").Replace("'", "\'");
                        parts.Add($"{lastLabel}drawtext=fontsize={ov.FontSize}:" +
                                  $"fontcolor={ov.Color}:text='{cn}':" +
                                  $"x={ov.X}:y={ov.Y}{next}");
                        lastLabel = next;
                        break;

                    case OverlayType.Cursor:
                    {
                        // Draw a cursor-dot overlay using FFmpeg's mouse= capture option
                        // (for desktop/screen sources) combined with a drawbox on the
                        // cursor position file written by CursorTracker.
                        //
                        // For screen-capture sources (gdigrab) FFmpeg has native
                        // draw_mouse=1 support — we enable that directly.
                        // For webcam / file sources we use a drawbox driven by a
                        // small C# polling thread that writes cursor XY to a temp file,
                        // combined with FFmpeg's 'sendcmd' filter to reposition each frame.
                        //
                        // Implementation: inject a drawbox whose coordinates are rewritten
                        // per-frame from the position file via a sendcmd script file.
                        //
                        // CursorTracker writes:  /tmp/visioncore_cursor.txt  -> "x,y\n"
                        // CursorSendcmd writes:  /tmp/visioncore_cursor_cmd.txt (sendcmd script)

                        if (config.Source == CameraSource.Screen ||
                            config.Source == CameraSource.AppWindow)
                        {
                            // gdigrab already has draw_mouse=1 in the input args.
                            // No extra filter needed — just note it for the caller.
                            // The draw_mouse flag is set in BuildInputArgs.
                        }
                        else
                        {
                            // Non-desktop sources: drawbox at cursor position using
                            // a sendcmd script refreshed by CursorSendcmdWriter.
                            var cmdFile = CursorTracker.GetSendcmdFilePath()
                                              .Replace("\\", "/");
                            var r  = ov.CursorSize / 2;
                            var sz = ov.CursorSize;
                            parts.Add(
                                $"{lastLabel}sendcmd=f='{cmdFile}'," +
                                $"drawbox=x=0:y=0:w={sz}:h={sz}:color={ov.Color}@0.8:t=fill{next}");
                            lastLabel = next;
                        }

                        // Start the cursor-tracking polling thread.
                        CursorTracker.AddRef();
                        break;
                    }
                }
            }

            if (parts.Count == 0) return "";

            // Rename final label to [out]
            var last = parts[^1];
            parts[^1] = last[..last.LastIndexOf('[')] + "[out]";

            return $"-filter_complex \"{string.Join("; ", parts)}\" -map \"[out]\" ";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static (int w, int h) ResolutionPixels(Resolution r) => r switch
        {
            Resolution.R360p  => ( 640,  360),
            Resolution.R480p  => ( 854,  480),
            Resolution.R720p  => (1280,  720),
            Resolution.R1080p => (1920, 1080),
            Resolution.R4K    => (3840, 2160),
            _                 => (1920, 1080),
        };

        /// <summary>
        /// Auto-generate credentials if config has none.
        /// Publisher uses a separate internal password; clients use config.Username/Password.
        /// </summary>
        private static void EnsureCredentials(CameraConfig config)
        {
            // FIX: generate password regardless of whether Username is set
            if (string.IsNullOrEmpty(config.Password))
                config.Password = GeneratePassword();
        }

        private static string GeneratePassword(int length = 16)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            return System.Security.Cryptography.RandomNumberGenerator.GetString(chars, length);
        }

        internal static string FindFfmpeg()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            return File.Exists(local) ? local : "ffmpeg";
        }

        public void Dispose()
        {
            _statsTimer?.Dispose();
            StopAllAsync().GetAwaiter().GetResult();
        }
    }
}
