using System;

namespace VisionCore.Core.Models
{
    /// <summary>
    /// Live runtime statistics for a single virtual camera stream.
    ///
    /// Created by <c>CameraManager.StartCameraAsync</c> and updated every
    /// <c>StatsIntervalMs</c> (2 s) by the stats-refresh timer.
    /// Consumed by:
    ///   • <c>ICameraManager.GetStats</c> / <c>GetAllStats</c>
    ///   • <c>DashboardViewModel</c> tick — projected into <c>CameraRowViewModel</c>
    ///   • REST API <c>/api/v1/cameras/{id}/stats</c>
    ///
    /// This class is mutated in-place by the stats timer (lock-free because the
    /// timer never recreates the instance — only updates its fields).
    /// Callers that snapshot the values for display should read them once.
    /// </summary>
    public sealed class CameraStats
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>Camera this stats object belongs to.</summary>
        public Guid CameraId { get; set; }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>True while the FFmpeg publisher process is alive.</summary>
        public bool IsRunning { get; set; }

        /// <summary>UTC time the camera was last started.</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>
        /// Elapsed time since <see cref="StartedAt"/>.
        /// Updated by the stats timer. Formatted as <c>hh:mm:ss</c> in the dashboard.
        /// </summary>
        public TimeSpan Uptime { get; set; }

        // ── Clients ───────────────────────────────────────────────────────────

        /// <summary>
        /// Number of RTSP clients currently connected to this camera's stream.
        /// Polled from the MediaMTX API by <c>RtspServer.GetClientCount</c>.
        /// </summary>
        public int ActiveClients { get; set; }

        // ── Encoding performance ──────────────────────────────────────────────

        /// <summary>
        /// Current output bitrate in kbps as reported by the FFmpeg publisher.
        /// Updated from the publisher process log or MediaMTX metrics.
        /// </summary>
        public double CurrentBitrateKbps { get; set; }

        /// <summary>
        /// Actual frames-per-second being encoded and pushed to MediaMTX.
        /// May be lower than the configured <see cref="CameraConfig.FrameRate"/>
        /// if the system is under load.
        /// </summary>
        public double Fps { get; set; }

        /// <summary>
        /// CPU utilisation (%) of the FFmpeg publisher process.
        /// Computed as a delta of <c>Process.TotalProcessorTime</c> between ticks.
        /// </summary>
        public double CpuUsage { get; set; }

        /// <summary>
        /// Display name of the active hardware encoder, e.g. <c>"NVENC H.264"</c>.
        /// Empty or null when software encoding is used (shown as <c>"CPU"</c> in the UI).
        /// </summary>
        public string? GpuEncoder { get; set; }


        // ── Throughput counters ───────────────────────────────────────────────

        /// <summary>Total video frames encoded since the stream started.</summary>
        public long FramesEncoded { get; set; }

        /// <summary>Total bytes sent to RTSP clients since the stream started.</summary>
        public long BytesSent { get; set; }

        // ── Motion ────────────────────────────────────────────────────────────

        /// <summary>
        /// True if motion was detected in the most recent analysis frame.
        /// Updated by <c>MotionDetector</c> via the <c>MotionDetected</c> event.
        /// </summary>
        public bool MotionDetected { get; set; }

        /// <summary>
        /// Fraction of pixels that changed in the last motion analysis (0–1).
        /// Useful for dashboards that want to show a "motion level" bar.
        /// </summary>
        public double MotionRatio { get; set; }
    }
}
