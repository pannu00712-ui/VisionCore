using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Records each camera's RTSP stream to local MP4 files using FFmpeg.
    ///
    /// Architecture:
    ///   For each active recording, FFmpeg pulls from MediaMTX via the
    ///   local RTSP URL and writes to disk with segment muxing:
    ///
    ///   MediaMTX (rtsp://127.0.0.1:8554/{path})
    ///       → FFmpeg segment muxer
    ///           → {OutputFolder}/{CameraName}/{camera}_{date}_{time}.mp4
    ///
    /// Segmentation:
    ///   Uses FFmpeg's <c>segment</c> muxer with <c>reset_timestamps=1</c>.
    ///   Each segment is a self-contained MP4 playable without the others.
    ///
    /// Retention:
    ///   A background timer runs hourly and deletes files older than
    ///   <see cref="RecordingConfig.RetentionDays"/> or exceeding
    ///   <see cref="RecordingConfig.MaxDiskMb"/>.
    /// </summary>
    public sealed class LocalRecordingService : IDisposable
    {
        private readonly ILogger<LocalRecordingService> _logger;

        // cameraId → FFmpeg recording process
        private readonly ConcurrentDictionary<Guid, RecordingSession> _sessions = new();

        // Retention cleanup timer
        private readonly System.Threading.Timer _retentionTimer;
        private bool _disposed;

        // We hold a reference to all configs for the retention sweep
        private readonly ConcurrentDictionary<Guid, (CameraConfig cam, RecordingConfig cfg)> _configs = new();

        public LocalRecordingService(ILogger<LocalRecordingService> logger)
        {
            _logger = logger;
            _retentionTimer = new System.Threading.Timer(
                _ => EnforceRetentionAll(),
                null,
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(1));
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Start recording a camera stream.
        /// No-op if recording is already running for this camera.
        /// </summary>
        public async Task StartAsync(CameraConfig cam, RecordingConfig cfg, CancellationToken ct = default)
        {
            if (!cfg.Enabled) return;
            if (_sessions.ContainsKey(cam.Id))
            {
                _logger.LogDebug("Recording already running for '{Name}'.", cam.Name);
                return;
            }

            var outputDir = cfg.ResolveOutputFolder(cam.Name);
            Directory.CreateDirectory(outputDir);

            _configs[cam.Id] = (cam, cfg);

            var args = BuildArgs(cam, cfg, outputDir);
            _logger.LogDebug("[Recording/{Name}] FFmpeg args: {Args}", cam.Name, args);

            var psi = new ProcessStartInfo
            {
                FileName              = FindFfmpeg(),
                Arguments             = args,
                UseShellExecute       = false,
                CreateNoWindow        = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    _logger.LogDebug("[FFmpegRec/{Name}] {Line}", cam.Name, e.Data);
            };
            proc.Exited += (_, _) =>
            {
                _sessions.TryRemove(cam.Id, out _);
                if (!ct.IsCancellationRequested)
                    _logger.LogWarning("Recording process for '{Name}' exited unexpectedly.", cam.Name);
            };

            proc.Start();
            proc.BeginErrorReadLine();

            _sessions[cam.Id] = new RecordingSession
            {
                CameraId  = cam.Id,
                Process   = proc,
                StartedAt = DateTime.UtcNow,
                OutputDir = outputDir,
                Config    = cfg,
            };

            await Task.Delay(400, ct);
            _logger.LogInformation(
                "Recording started for '{Name}' → {Dir}", cam.Name, outputDir);
        }

        /// <summary>Stop recording for a specific camera.</summary>
        public async Task StopAsync(Guid cameraId)
        {
            if (!_sessions.TryRemove(cameraId, out var session)) return;

            if (!session.Process.HasExited)
            {
                try
                {
                    // Send 'q' to FFmpeg stdin for graceful MP4 finalisation
                    session.Process.StandardInput?.Write('q');
                    await Task.Run(() => session.Process.WaitForExit(5000));
                    if (!session.Process.HasExited)
                        session.Process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping recording process.");
                }
            }

            session.Process.Dispose();
            _configs.TryRemove(cameraId, out _);
            _logger.LogInformation("Recording stopped for camera {Id}.", cameraId);
        }

        /// <summary>Stop all active recordings.</summary>
        public async Task StopAllAsync()
        {
            foreach (var id in _sessions.Keys)
                await StopAsync(id);
        }

        /// <summary>True if recording is currently active for this camera.</summary>
        public bool IsRecording(Guid cameraId)
            => _sessions.TryGetValue(cameraId, out var s) && !s.Process.HasExited;

        // ── Argument builder ──────────────────────────────────────────────────

        private static string BuildArgs(CameraConfig cam, RecordingConfig cfg, string outputDir)
        {
            var sb = new StringBuilder();
            sb.Append("-y -loglevel warning ");

            // Input: pull from local MediaMTX
            var rtspUrl = RtspStreamManager.GetClientUrl(cam);
            sb.Append($"-i \"{rtspUrl}\" ");

            // Copy streams (no re-encode — preserves quality, minimal CPU)
            sb.Append("-c copy ");

            // Audio
            if (!cfg.RecordAudio || cam.AudioSource == AudioSource.None)
                sb.Append("-an ");

            if (cfg.Mode == RecordingMode.Segmented)
            {
                // Segment muxer
                var template = Path.Combine(
                    outputDir,
                    cfg.ResolveFileName(cam.Name, DateTime.Now) + "_%03d.mp4");

                var segSecs = cfg.SegmentMinutes * 60;
                sb.Append($"-f segment -segment_time {segSecs} -segment_format mp4 ");
                sb.Append("-reset_timestamps 1 -strftime 1 ");
                sb.Append($"\"{template}\"");
            }
            else
            {
                // Continuous — single file
                var filePath = Path.Combine(
                    outputDir,
                    cfg.ResolveFileName(cam.Name, DateTime.Now) + ".mp4");
                sb.Append($"\"{filePath}\"");
            }

            return sb.ToString().Trim();
        }

        // ── Retention ─────────────────────────────────────────────────────────

        private void EnforceRetentionAll()
        {
            foreach (var (_, (cam, cfg)) in _configs)
            {
                try
                {
                    EnforceRetention(cam, cfg);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Retention sweep failed for '{Name}'.", cam.Name);
                }
            }
        }

        private void EnforceRetention(CameraConfig cam, RecordingConfig cfg)
        {
            var dir = cfg.ResolveOutputFolder(cam.Name);
            if (!Directory.Exists(dir)) return;

            var files = new DirectoryInfo(dir)
                .GetFiles("*.mp4")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            // Delete by age
            if (cfg.RetentionDays > 0)
            {
                var cutoff = DateTime.UtcNow.AddDays(-cfg.RetentionDays);
                foreach (var f in files.Where(f => f.CreationTimeUtc < cutoff))
                {
                    SafeDelete(f);
                    _logger.LogInformation("Retention: deleted old recording {File}.", f.Name);
                }
            }

            // Delete by disk quota (oldest first)
            if (cfg.MaxDiskMb > 0)
            {
                var maxBytes = cfg.MaxDiskMb * 1024L * 1024L;
                var current  = files.Sum(f => f.Length);
                foreach (var f in files.OrderBy(f => f.CreationTimeUtc))
                {
                    if (current <= maxBytes) break;
                    current -= f.Length;
                    SafeDelete(f);
                    _logger.LogInformation("Retention: disk quota — deleted {File}.", f.Name);
                }
            }
        }

        private void SafeDelete(FileInfo f)
        {
            try { f.Delete(); }
            catch (Exception ex)
            { _logger.LogDebug(ex, "Could not delete {File}.", f.FullName); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FindFfmpeg()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            return File.Exists(local) ? local : "ffmpeg";
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _retentionTimer.Dispose();
            StopAllAsync().GetAwaiter().GetResult();
        }

        // ── Inner types ───────────────────────────────────────────────────────

        private sealed class RecordingSession
        {
            public Guid            CameraId  { get; init; }
            public Process         Process   { get; init; } = null!;
            public DateTime        StartedAt { get; init; }
            public string          OutputDir { get; init; } = string.Empty;
            public RecordingConfig Config    { get; init; } = null!;
        }
    }
}
