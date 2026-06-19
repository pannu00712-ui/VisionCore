using System;

namespace VisionCore.Core.Models
{
    /// <summary>
    /// Per-camera local recording configuration.
    ///
    /// Stored as a JSON property inside <see cref="CameraConfig"/> (add
    /// <c>public RecordingConfig Recording { get; set; } = new();</c> to CameraConfig).
    ///
    /// Used by:
    ///   • <c>LocalRecordingService</c>   — start/stop MP4 segment writers
    ///   • <c>RecordingViewModel</c>      — settings UI binding
    ///   • REST API                       — GET /api/v1/cameras/{id}/recording
    /// </summary>
    public sealed class RecordingConfig
    {
        // ── Enable / disable ──────────────────────────────────────────────────

        /// <summary>Whether local recording is enabled for this camera.</summary>
        public bool Enabled { get; set; } = false;

        // ── Output location ───────────────────────────────────────────────────

        /// <summary>
        /// Root folder where recordings are written.
        /// Sub-folders are created per camera: <c>{OutputFolder}\{CameraName}\</c>.
        /// Defaults to <c>%USERPROFILE%\Videos\VisionCore</c> when null/empty.
        /// </summary>
        public string? OutputFolder { get; set; }

        /// <summary>
        /// File name template.
        /// Supports strftime tokens evaluated at segment start:
        ///   <c>{camera}</c> — camera name (sanitised)
        ///   <c>{date}</c>   — yyyyMMdd
        ///   <c>{time}</c>   — HHmmss
        /// Default: <c>"{camera}_{date}_{time}"</c>
        /// Extension is always <c>.mp4</c>.
        /// </summary>
        public string FileNameTemplate { get; set; } = "{camera}_{date}_{time}";

        // ── Segmentation ──────────────────────────────────────────────────────

        /// <summary>
        /// Recording mode: continuous single file, or fixed-length segments.
        /// </summary>
        public RecordingMode Mode { get; set; } = RecordingMode.Segmented;

        /// <summary>
        /// Duration of each MP4 segment in minutes (1–1440).
        /// Only used when <see cref="Mode"/> is <see cref="RecordingMode.Segmented"/>.
        /// Default: 60 minutes.
        /// </summary>
        public int SegmentMinutes { get; set; } = 60;

        // ── Retention ─────────────────────────────────────────────────────────

        /// <summary>
        /// Automatically delete recordings older than this many days.
        /// 0 = keep forever (manual deletion only).
        /// </summary>
        public int RetentionDays { get; set; } = 7;

        /// <summary>
        /// Maximum total disk usage for recordings of this camera, in megabytes.
        /// Oldest segments are deleted first when the limit is reached.
        /// 0 = no disk limit enforced.
        /// </summary>
        public long MaxDiskMb { get; set; } = 0;

        // ── Trigger ───────────────────────────────────────────────────────────

        /// <summary>
        /// When to start recording.
        /// <see cref="RecordingTrigger.Always"/> — record whenever the camera is running.
        /// <see cref="RecordingTrigger.MotionOnly"/> — start on motion, stop after
        ///   <see cref="PostMotionSeconds"/> of inactivity.
        /// <see cref="RecordingTrigger.Schedule"/> — governed by <see cref="ScheduleRules"/>
        ///   on the parent <see cref="CameraConfig"/> (NYI, placeholder for SchedulerService).
        /// </summary>
        public RecordingTrigger Trigger { get; set; } = RecordingTrigger.Always;

        /// <summary>
        /// Seconds to continue recording after the last motion event before stopping.
        /// Only relevant when <see cref="Trigger"/> is <see cref="RecordingTrigger.MotionOnly"/>.
        /// Default: 30 seconds.
        /// </summary>
        public int PostMotionSeconds { get; set; } = 30;

        // ── Audio ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether to include the audio track in recordings.
        /// Has no effect if <see cref="CameraConfig.AudioSource"/> is
        /// <see cref="AudioSource.None"/>.
        /// </summary>
        public bool RecordAudio { get; set; } = true;

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Resolve the effective output folder, substituting the default when the
        /// configured value is null or empty.
        /// </summary>
        public string ResolveOutputFolder(string cameraName)
        {
            var root = string.IsNullOrWhiteSpace(OutputFolder)
                ? System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "VisionCore")
                : OutputFolder;

            var safe = SanitiseName(cameraName);
            return System.IO.Path.Combine(root, safe);
        }

        /// <summary>
        /// Build a concrete file name (without extension) from the template.
        /// </summary>
        public string ResolveFileName(string cameraName, DateTime segmentStart)
        {
            return FileNameTemplate
                .Replace("{camera}", SanitiseName(cameraName))
                .Replace("{date}",   segmentStart.ToString("yyyyMMdd"))
                .Replace("{time}",   segmentStart.ToString("HHmmss"));
        }

        private static string SanitiseName(string name)
        {
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            return name;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Supporting enums
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>How recordings are segmented on disk.</summary>
    public enum RecordingMode
    {
        /// <summary>One file per camera session (stops when the camera stops).</summary>
        Continuous,

        /// <summary>Fixed-duration MP4 segments (see <see cref="RecordingConfig.SegmentMinutes"/>).</summary>
        Segmented,
    }

    /// <summary>When recording starts and stops.</summary>
    public enum RecordingTrigger
    {
        /// <summary>Record the full stream continuously while the camera is running.</summary>
        Always,

        /// <summary>Start recording on motion; stop after a quiet period.</summary>
        MotionOnly,

        /// <summary>Record according to the camera's schedule rules.</summary>
        Schedule,
    }
}
