using System;
using System.Collections.Generic;

namespace VisionCore.Core.Models
{
    /// <summary>
    /// Persisted configuration for a single virtual camera.
    ///
    /// Stored as a JSON blob in the SQLite <c>cameras</c> table (keyed by <see cref="Id"/>).
    /// Referenced by every major subsystem:
    ///   • <c>SettingsService</c>   — load / save / delete
    ///   • <c>CameraManager</c>     — start / stop orchestration
    ///   • <c>RtspStreamManager</c> — FFmpeg publisher arguments
    ///   • <c>RtspServer</c>        — stream path registration
    ///   • <c>OnvifServer</c>       — per-camera HTTP endpoint
    ///   • <c>MotionDetector</c>    — per-camera motion config
    ///   • REST API                 — request / response bodies
    ///   • WPF ViewModels           — binding source for edit dialog and dashboard
    /// </summary>
    public sealed class CameraConfig
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>Stable unique identifier. Never changes after creation.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Human-readable display name shown in the dashboard and ONVIF discovery.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Whether this camera should auto-start when the service starts.</summary>
        public bool Enabled { get; set; } = true;

        // ── Source ────────────────────────────────────────────────────────────

        /// <summary>Capture source type (screen region, webcam, or combined).</summary>
        public CameraSource Source { get; set; } = CameraSource.Screen;

        /// <summary>
        /// Device identifier for webcam sources.
        /// Format: DirectShow device name or index, e.g. <c>"@device:pnp:\\?\usb#..."</c>.
        /// Null for screen-capture sources.
        /// </summary>
        public string? WebcamDeviceId { get; set; }

        /// <summary>
        /// Title (or substring) of the application window to capture.
        /// Used when <see cref="Source"/> is <see cref="CameraSource.AppWindow"/>.
        /// DeskCamera-style: partial match is allowed (e.g. "Notepad" matches any Notepad window).
        /// Null means use the first visible top-level window found.
        /// </summary>
        public string? WindowTitle { get; set; }

        /// <summary>
        /// Absolute path to the image file (JPEG or PNG) to loop as a static stream.
        /// Used when <see cref="Source"/> is <see cref="CameraSource.StaticImage"/>.
        /// FFmpeg reads this file and re-encodes it at <see cref="FrameRate"/> FPS.
        /// Null or missing file causes the publisher to log an error and skip start.
        /// </summary>
        public string? StaticImagePath { get; set; }

        /// <summary>
        /// Input URL for external RTSP or HTTP sources.
        /// Used when <see cref="Source"/> is <see cref="CameraSource.ExternalRtsp"/>
        /// or <see cref="CameraSource.ExternalHttp"/>.
        /// Example: <c>rtsp://192.168.1.10:554/stream</c> or <c>http://cam/mjpeg</c>.
        /// </summary>
        public string? InputUrl { get; set; }

        /// <summary>
        /// Screen region to capture.
        /// Null means full primary monitor.
        /// Only used when <see cref="Source"/> is <see cref="CameraSource.Screen"/>
        /// or <see cref="CameraSource.Combined"/>.
        /// </summary>
        public ScreenRegion? Region { get; set; }

        // ── RTSP stream ───────────────────────────────────────────────────────

        /// <summary>
        /// Path segment appended to the MediaMTX RTSP URL.
        /// Example: <c>"office-cam"</c> → <c>rtsp://host:8554/office-cam</c>.
        /// Must be URL-safe (no spaces; use hyphens).
        /// </summary>
        public string RtspPath { get; set; } = string.Empty;

        /// <summary>Port MediaMTX listens on for RTSP connections (default 8554).</summary>
        public int RtspPort { get; set; } = 8554;

        // ── Video encoding ────────────────────────────────────────────────────

        /// <summary>Output video codec.</summary>
        public VideoCodec Codec { get; set; } = VideoCodec.H264;

        /// <summary>Output resolution preset.</summary>
        public Resolution Resolution { get; set; } = Resolution.R1080p;

        /// <summary>Frames per second (1–120).</summary>
        public int FrameRate { get; set; } = 30;

        /// <summary>Target video bitrate in kbps (100–100 000).</summary>
        public int Bitrate { get; set; } = 4000;

        /// <summary>Hardware encoder to use, or <see cref="GpuAccel.None"/> for software x264/x265.</summary>
        public GpuAccel GpuAccel { get; set; } = GpuAccel.None;

        // ── Audio ─────────────────────────────────────────────────────────────

        /// <summary>Audio input source.</summary>
        public AudioSource AudioSource { get; set; } = AudioSource.None;

        /// <summary>
        /// DirectShow microphone device name.
        /// Null when <see cref="AudioSource"/> is <see cref="Models.AudioSource.None"/>.
        /// </summary>
        public string? MicDeviceId { get; set; }

        /// <summary>Audio bitrate in kbps (default 128).</summary>
        public int AudioBitrate { get; set; } = 128;

        /// <summary>
        /// Audio codec used for the outgoing RTSP stream.
        /// Defaults to <see cref="AudioCodec.AAC"/> for broad NVR compatibility.
        /// Switch to <see cref="AudioCodec.G711ULaw"/> for ONVIF Profile S mandatory compliance
        /// or when connecting to older NVRs that do not support AAC.
        /// </summary>
        public AudioCodec AudioCodec { get; set; } = AudioCodec.AAC;

        // ── ONVIF ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether to expose an ONVIF Profile S endpoint for this camera.
        /// When true, <c>OnvifServer</c> starts an HTTP listener on <see cref="OnvifPort"/>.
        /// </summary>
        public bool OnvifEnabled { get; set; } = true;

        /// <summary>Port for the per-camera ONVIF HTTP listener (1024–65535).</summary>
        public int OnvifPort { get; set; } = 8080;

        // ── Motion detection ──────────────────────────────────────────────────

        /// <summary>
        /// Whether frame-differencing motion detection is enabled for this camera.
        /// Detailed tuning is done via <see cref="MotionConfig"/> passed to <c>MotionDetector</c>.
        /// </summary>
        public bool MotionDetection { get; set; } = false;

        /// <summary>Per-pixel difference threshold (0–255). Stored here for UI binding convenience.</summary>
        public int MotionThreshold { get; set; } = 25;

        /// <summary>
        /// Fraction of pixels that must change to trigger a motion event (0–1).
        /// Stored here for UI binding; the runtime value is in <see cref="MotionConfig"/>.
        /// </summary>
        public double MotionRatio { get; set; } = 0.02;


        // ── Input-activity motion trigger ─────────────────────────────────────

        /// <summary>
        /// When true, any keyboard or mouse activity is treated as a motion event
        /// for this camera — in addition to (or instead of) frame-differencing.
        /// Uses <see cref="InputActivityMonitor"/> which installs low-level Win32 hooks.
        /// </summary>
        public bool MotionOnInputActivity { get; set; } = false;

        /// <summary>
        /// How long after the last keyboard/mouse event the motion state remains
        /// active before automatically clearing.  Default is 5 seconds.
        /// </summary>
        public TimeSpan InputMotionHoldTime { get; set; } = TimeSpan.FromSeconds(5);

        // ── Scheduling ────────────────────────────────────────────────────────

        /// <summary>
        /// Optional list of time-window rules that control when this camera is
        /// allowed to stream. Empty list means always allowed (no schedule enforced).
        /// Evaluated by <c>SchedulerService</c> every 30 seconds.
        /// </summary>
        public List<ScheduleRule> ScheduleRules { get; set; } = new();

        // ── Authentication ────────────────────────────────────────────────────

        /// <summary>
        /// RTSP stream username for basic auth.
        /// Null or empty means no authentication.
        /// </summary>
        public string? Username { get; set; }

        /// <summary>RTSP stream password. Null or empty means no authentication.</summary>
        public string? Password { get; set; }

        // ── Overlays ──────────────────────────────────────────────────────────

        /// <summary>
        /// Ordered list of overlay elements burned into the video by FFmpeg drawtext/overlay filters.
        /// </summary>
        public List<OverlayConfig> Overlays { get; set; } = new();

        // ── Rotation ──────────────────────────────────────────────────────────

        /// <summary>
        /// Rotation / flip applied to the video before encoding.
        /// Mirrors DeskCamera rotation options (Auto, None, R90, L90, FlipH, FlipV, 180).
        /// Implemented as an FFmpeg video filter inserted before the encoder.
        /// Default is <see cref="VideoRotation.None"/> (pass-through).
        /// </summary>
        public VideoRotation Rotation { get; set; } = VideoRotation.None;

        // ── Local recording ───────────────────────────────────────────────────

        /// <summary>
        /// Local MP4 recording configuration for this camera.
        /// Controls whether recordings are saved to disk, output folder,
        /// segmentation, retention policy, and motion-triggered recording.
        /// Evaluated by <c>LocalRecordingService</c>.
        /// </summary>
        public RecordingConfig Recording { get; set; } = new();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Enumerations used by CameraConfig
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Where the video feed originates from.</summary>
    public enum CameraSource
    {
        /// <summary>Capture a region of the desktop using GDI / DXGI.</summary>
        Screen,

        /// <summary>Capture from a physical webcam via DirectShow.</summary>
        Webcam,

        /// <summary>Composite: screen region with webcam picture-in-picture.</summary>
        Combined,

        /// <summary>Re-stream an external RTSP source through ONVIF.</summary>
        ExternalRtsp,

        /// <summary>Re-stream an external HTTP/MJPEG source through ONVIF.</summary>
        ExternalHttp,

        /// <summary>Capture a specific application window by partial title match (Windows 10 1903+).</summary>
        AppWindow,

        /// <summary>
        /// Serve a static JPEG/PNG image file as a never-ending RTSP stream.
        /// Useful when a real camera is temporarily unavailable but the NVR channel
        /// must remain active.  FFmpeg loops the image at the configured frame-rate.
        /// Mirrors DeskCamera's "Static image stream" feature.
        /// </summary>
        StaticImage,

        /// <summary>
        /// Publish an audio-only RTSP stream with no video track.
        /// Useful for microphones or audio monitors that don't need a picture.
        /// Mirrors DeskCamera's "Audio-only stream" feature.
        /// </summary>
        AudioOnly,
    }

    /// <summary>Output video codec passed to FFmpeg.</summary>
    public enum VideoCodec
    {
        /// <summary>H.264 / AVC — widest compatibility.</summary>
        H264,

        /// <summary>H.265 / HEVC — higher compression, slightly less compatible.</summary>
        H265,

        /// <summary>
        /// Motion JPEG — each frame is an independent JPEG image.
        /// Higher bandwidth than H.264/H.265 but zero inter-frame latency and
        /// compatible with older NVRs that do not support MPEG codecs.
        /// Software-only (no hardware acceleration path).
        /// </summary>
        MJPEG,
    }

    /// <summary>Common resolution presets. The encoder applies the closest supported mode.</summary>
    public enum Resolution
    {
        /// <summary>1920 × 1080</summary>
        R1080p,

        /// <summary>1280 × 720</summary>
        R720p,

        /// <summary>854 × 480</summary>
        R480p,

        /// <summary>640 × 360</summary>
        R360p,

        /// <summary>3840 × 2160 — requires a capable GPU encoder.</summary>
        R4K,
    }

    /// <summary>Hardware video encoder to use (if available).</summary>
    public enum GpuAccel
    {
        /// <summary>Software encoder (libx264 / libx265). Always available; highest CPU cost.</summary>
        None,

        /// <summary>NVIDIA NVENC — H.264 (h264_nvenc).</summary>
        NvencH264,

        /// <summary>NVIDIA NVENC — H.265 (hevc_nvenc).</summary>
        NvencH265,

        /// <summary>Intel Quick Sync — H.264 (h264_qsv).</summary>
        QuickSync,

        /// <summary>AMD AMF — H.264 (h264_amf).</summary>
        Amd,
    }

    /// <summary>Audio capture source for the stream.</summary>
    public enum AudioSource
    {
        /// <summary>No audio track in the stream.</summary>
        None,

        /// <summary>Capture from a microphone / line-in via DirectShow.</summary>
        Microphone,

        /// <summary>Capture desktop/system audio (WASAPI loopback).</summary>
        DesktopAudio,
    }

    /// <summary>
    /// Audio codec used for the outgoing RTSP stream.
    /// Mirrors DeskCamera supported audio formats (G711 u-law, AAC).
    /// </summary>
    public enum AudioCodec
    {
        /// <summary>
        /// AAC (Advanced Audio Coding) — good quality at low bitrates.
        /// Widely supported by modern NVRs and VMSes.
        /// FFmpeg encoder: <c>aac</c>.
        /// </summary>
        AAC,

        /// <summary>
        /// G.711 µ-law (PCM mu-law, 8 kHz, 64 kbps).
        /// Mandatory codec in ONVIF Profile S; required by many older NVRs.
        /// FFmpeg encoder: <c>pcm_mulaw</c>, muxed in RTP payload type 0.
        /// </summary>
        G711ULaw,
    }

    /// <summary>
    /// Video rotation / flip applied by FFmpeg before encoding.
    /// Mirrors DeskCamera rotation options: Auto, None, R90, L90, FlipH, FlipV, 180.
    /// </summary>
    public enum VideoRotation
    {
        /// <summary>
        /// Automatically apply rotation metadata from the source (e.g. webcam sensor orientation).
        /// Uses FFmpeg <c>autorotate=1</c> input option.
        /// </summary>
        Auto,

        /// <summary>No rotation — pass frames through unmodified.</summary>
        None,

        /// <summary>Rotate 90° clockwise. FFmpeg transpose=1.</summary>
        R90,

        /// <summary>Rotate 90° counter-clockwise. FFmpeg transpose=2.</summary>
        L90,

        /// <summary>Flip horizontally (mirror left↔right). FFmpeg hflip.</summary>
        FlipH,

        /// <summary>Flip vertically (mirror top↔bottom). FFmpeg vflip.</summary>
        FlipV,

        /// <summary>Rotate 180°. FFmpeg transpose=2,transpose=2 or hflip+vflip.</summary>
        Rotate180,
    }
}
