using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VisionCore.Core.Models;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Per-camera motion detector using frame-differencing against a rolling background model.
    ///
    /// Pipeline:
    ///   RtspStreamManager snapshot  ──▶  MotionDetector  ──▶  MotionDetectedEvent
    ///
    /// Algorithm (lightweight, no OpenCV dependency):
    ///   1. Capture a grayscale thumbnail from the camera's RTSP stream via FFmpeg one-shot grab.
    ///   2. Diff the new frame against the previous background frame (pixel-level absolute difference).
    ///   3. Count pixels that exceed <see cref="PixelThreshold"/> — "changed pixels".
    ///   4. If changed pixels / total pixels > <see cref="MotionRatio"/> → fire MotionDetected event.
    ///   5. Update the background with a weighted blend so slow lighting changes don't trigger false positives.
    ///
    /// Tuning (per-camera overrides via <see cref="MotionConfig"/>):
    ///   PixelThreshold   — per-channel sensitivity (0–255, default 25)
    ///   MotionRatio      — fraction of frame that must change (0–1, default 0.02 = 2 %)
    ///   BackgroundAlpha  — background learning rate (0–1, default 0.05)
    ///   SampleInterval   — how often to grab a frame (default 1 s)
    ///   ThumbnailWidth   — analysis resolution width (default 320 px, keeps CPU low)
    /// </summary>
    public sealed class MotionDetector : IDisposable
    {
        // ── Dependencies ──────────────────────────────────────────────────────
        private readonly ILogger<MotionDetector> _logger;
        private readonly SettingsService _settings;

        // cameraId → detector state
        private readonly ConcurrentDictionary<Guid, CameraDetectorState> _states = new();

        // cameraId → running task
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cts = new();

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired when motion is detected on a camera, or cleared after a quiet period.</summary>
        public event EventHandler<MotionEventArgs>? MotionDetected;

        // ── Defaults ──────────────────────────────────────────────────────────
        public const int    DefaultPixelThreshold  = 25;
        public const double DefaultMotionRatio      = 0.02;
        public const double DefaultBackgroundAlpha  = 0.05;
        public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(1);
        public const int    DefaultThumbnailWidth   = 320;

        public MotionDetector(ILogger<MotionDetector> logger, SettingsService settings)
        {
            _logger   = logger;
            _settings = settings;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Start monitoring a camera for motion.</summary>
        public void StartCamera(CameraConfig cam, MotionConfig? cfg = null)
        {
            if (_cts.ContainsKey(cam.Id))
            {
                _logger.LogWarning("Motion detection already running for '{Name}'.", cam.Name);
                return;
            }

            cfg ??= MotionConfig.Default;
            var state = new CameraDetectorState(cam, cfg);
            _states[cam.Id] = state;

            var tokenSource = new CancellationTokenSource();
            _cts[cam.Id] = tokenSource;

            _ = Task.Run(() => MonitorLoopAsync(state, tokenSource.Token), tokenSource.Token);
            _logger.LogInformation(
                "Motion detection started for '{Name}' (interval={Interval}s, ratio={Ratio}).",
                cam.Name, cfg.SampleInterval.TotalSeconds, cfg.MotionRatio);
        }

        /// <summary>Stop monitoring a specific camera.</summary>
        public async Task StopCameraAsync(Guid cameraId)
        {
            if (_cts.TryRemove(cameraId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            _states.TryRemove(cameraId, out _);
            await Task.CompletedTask;
        }

        /// <summary>Stop all cameras.</summary>
        public async Task StopAllAsync()
        {
            foreach (var id in _cts.Keys)
                await StopCameraAsync(id);
        }

        /// <summary>Dynamically update sensitivity for a running camera.</summary>
        public void UpdateConfig(Guid cameraId, MotionConfig cfg)
        {
            if (_states.TryGetValue(cameraId, out var state))
                state.Config = cfg;
        }


        /// <summary>
        /// Externally inject a motion state change — used by
        /// <see cref="InputActivityMonitor"/> for keyboard/mouse-driven triggers
        /// and by the REST API manual trigger endpoint.
        /// The event is only raised when the state actually changes.
        /// </summary>
        public void TriggerInputMotion(Guid cameraId, bool isMotion)
        {
            // Reuse the same MotionDetected event so CameraManager/OnvifServer
            // get notified through the normal pipeline.
            var cameraName = _states.TryGetValue(cameraId, out var state)
                ? state.Camera.Name
                : cameraId.ToString()[..8];

            MotionDetected?.Invoke(this, new MotionEventArgs(
                cameraId,
                cameraName,
                isMotion,
                isMotion ? 1.0 : 0.0,   // ratio = 1.0 for input-triggered events
                DateTime.UtcNow));

            _logger.LogDebug(
                "[InputMotion] Camera {Id} motion {State} (input trigger).",
                cameraId, isMotion ? "ACTIVE" : "cleared");
        }

        // ── Core loop ─────────────────────────────────────────────────────────

        private async Task MonitorLoopAsync(CameraDetectorState state, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(state.Config.SampleInterval, ct);

                    var frame = await GrabThumbnailAsync(state.Camera, state.Config.ThumbnailWidth, ct);
                    if (frame == null) continue;

                    var result = Analyze(state, frame);

                    if (result.MotionDetected != state.LastMotionState)
                    {
                        state.LastMotionState = result.MotionDetected;
                        MotionDetected?.Invoke(this, new MotionEventArgs(
                            state.Camera.Id,
                            state.Camera.Name,
                            result.MotionDetected,
                            result.ChangedRatio,
                            DateTime.UtcNow));

                        _logger.LogInformation(
                            "Motion {State} on '{Name}' (changed={Ratio:P1}).",
                            result.MotionDetected ? "DETECTED" : "cleared",
                            state.Camera.Name, result.ChangedRatio);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Motion detection tick error for '{Name}'.", state.Camera.Name);
                }
            }
        }

        // ── Frame analysis ────────────────────────────────────────────────────

        private AnalysisResult Analyze(CameraDetectorState state, byte[] grayFrame)
        {
            var cfg = state.Config;

            // First frame — initialise background, no motion
            if (state.Background == null || state.Background.Length != grayFrame.Length)
            {
                state.Background = (byte[])grayFrame.Clone();
                return new AnalysisResult(false, 0);
            }

            var bg      = state.Background;
            int changed = 0;
            int total   = grayFrame.Length;

            for (int i = 0; i < total; i++)
            {
                int diff = Math.Abs(grayFrame[i] - bg[i]);
                if (diff > cfg.PixelThreshold)
                    changed++;

                // Update background via exponential moving average
                bg[i] = (byte)(bg[i] * (1 - cfg.BackgroundAlpha) + grayFrame[i] * cfg.BackgroundAlpha);
            }

            double ratio = (double)changed / total;
            return new AnalysisResult(ratio >= cfg.MotionRatio, ratio);
        }

        // ── FFmpeg single-frame grab ──────────────────────────────────────────

        /// <summary>
        /// Runs FFmpeg to grab one frame from the camera's local RTSP stream,
        /// outputs a raw grayscale byte array at the configured thumbnail width.
        /// </summary>
        private async Task<byte[]?> GrabThumbnailAsync(
            CameraConfig cam, int thumbWidth, CancellationToken ct)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"vc_motion_{cam.Id:N}.bmp");

            try
            {
                // Height derived from 16:9; adjust if cam aspect differs
                int thumbHeight = thumbWidth * 9 / 16;

                var rtspUrl = RtspStreamManager.GetClientUrl(cam);
                var args    = $"-rtsp_transport tcp -i \"{rtspUrl}\" " +
                              $"-vf \"scale={thumbWidth}:{thumbHeight},format=gray\" " +
                              $"-vframes 1 -y \"{tempFile}\"";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName              = FindFfmpeg(),
                    Arguments             = args,
                    UseShellExecute       = false,
                    CreateNoWindow        = true,
                    RedirectStandardError = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to start FFmpeg.");

                await Task.Run(() => proc.WaitForExit(5000), ct);

                if (!File.Exists(tempFile))
                {
                    _logger.LogDebug("FFmpeg grab produced no output for '{Name}'.", cam.Name);
                    return null;
                }

                return ExtractGrayPixels(tempFile, thumbWidth, thumbHeight);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Frame grab failed for '{Name}'.", cam.Name);
                return null;
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best-effort */ }
            }
        }

        /// <summary>
        /// Reads a BMP file and extracts a flat grayscale pixel array
        /// without taking an OpenCV dependency.
        /// </summary>
        private static byte[] ExtractGrayPixels(string bmpPath, int w, int h)
        {
            using var bmp    = new Bitmap(bmpPath);
            var data         = bmp.LockBits(
                                    new Rectangle(0, 0, w, h),
                                    ImageLockMode.ReadOnly,
                                    PixelFormat.Format24bppRgb);
            var pixels       = new byte[w * h];
            int stride       = data.Stride;
            var ptr          = data.Scan0;
            var raw          = new byte[stride * h];
            Marshal.Copy(ptr, raw, 0, raw.Length);
            bmp.UnlockBits(data);

            // Convert RGB → luminance  (ITU-R BT.601)
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int offset  = y * stride + x * 3;
                byte b      = raw[offset];
                byte g      = raw[offset + 1];
                byte r      = raw[offset + 2];
                pixels[y * w + x] = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
            }

            return pixels;
        }

        private static string FindFfmpeg()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            return File.Exists(local) ? local : "ffmpeg";
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            StopAllAsync().GetAwaiter().GetResult();
        }

        // ── Nested types ──────────────────────────────────────────────────────

        private sealed class CameraDetectorState
        {
            public CameraConfig  Camera           { get; }
            public MotionConfig  Config           { get; set; }
            public byte[]?       Background       { get; set; }
            public bool          LastMotionState  { get; set; }

            public CameraDetectorState(CameraConfig cam, MotionConfig cfg)
            {
                Camera = cam;
                Config = cfg;
            }
        }

        private readonly record struct AnalysisResult(bool MotionDetected, double ChangedRatio);
    }

    // ── Supporting types ──────────────────────────────────────────────────────

    /// <summary>Per-camera motion detection configuration.</summary>
    public sealed class MotionConfig
    {
        /// <summary>Per-pixel grayscale difference required to count as "changed" (0–255).</summary>
        public int PixelThreshold { get; set; } = MotionDetector.DefaultPixelThreshold;

        /// <summary>Fraction of frame pixels that must change to fire a motion event (0–1).</summary>
        public double MotionRatio { get; set; } = MotionDetector.DefaultMotionRatio;

        /// <summary>Background learning rate — higher = adapts faster to lighting changes (0–1).</summary>
        public double BackgroundAlpha { get; set; } = MotionDetector.DefaultBackgroundAlpha;

        /// <summary>How often to sample a frame from the stream.</summary>
        public TimeSpan SampleInterval { get; set; } = MotionDetector.DefaultSampleInterval;

        /// <summary>Width of the thumbnail used for analysis (height is derived 16:9).</summary>
        public int ThumbnailWidth { get; set; } = MotionDetector.DefaultThumbnailWidth;

        public static MotionConfig Default => new();

        /// <summary>A more sensitive preset — useful for low-traffic areas.</summary>
        public static MotionConfig Sensitive => new()
        {
            PixelThreshold = 15,
            MotionRatio    = 0.005,
        };

        /// <summary>A less sensitive preset — useful for busy areas to reduce noise.</summary>
        public static MotionConfig Relaxed => new()
        {
            PixelThreshold = 40,
            MotionRatio    = 0.05,
        };
    }

    /// <summary>Event payload raised by <see cref="MotionDetector"/>.</summary>
    public sealed record MotionEventArgs(
        Guid     CameraId,
        string   CameraName,
        bool     IsMotion,
        double   ChangedRatio,
        DateTime Timestamp);
}
