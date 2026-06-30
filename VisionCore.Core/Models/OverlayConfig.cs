namespace VisionCore.Core.Models
{
    /// <summary>
    /// Configuration for a single overlay element burned into the video stream
    /// by an FFmpeg <c>drawtext</c> or <c>overlay</c> filter.
    ///
    /// A <see cref="CameraConfig"/> can have zero or more overlays in its
    /// <c>Overlays</c> list. They are applied in list order (later entries
    /// appear on top of earlier ones).
    ///
    /// Used by:
    ///   • <c>CameraConfig.Overlays</c>         — persisted with the camera
    ///   • <c>RtspStreamManager</c>             — converted to FFmpeg filter args
    ///   • <c>OverlayRowViewModel</c>           — two-way bound in the camera edit dialog
    /// </summary>
    public sealed class OverlayConfig
    {
        // ── Type ──────────────────────────────────────────────────────────────

        /// <summary>What kind of content this overlay renders.</summary>
        public OverlayType Type { get; set; } = OverlayType.Timestamp;

        // ── Position ──────────────────────────────────────────────────────────

        /// <summary>
        /// Horizontal position of the overlay anchor, in pixels from the left edge
        /// of the video frame.
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// Vertical position of the overlay anchor, in pixels from the top edge
        /// of the video frame.
        /// </summary>
        public int Y { get; set; }

        // ── Appearance ────────────────────────────────────────────────────────

        /// <summary>Font size in points for text overlays.</summary>
        public int FontSize { get; set; } = 16;

        /// <summary>
        /// CSS-style colour name or hex value (e.g. <c>"white"</c>, <c>"#FFFF00"</c>).
        /// Accepted by FFmpeg's <c>drawtext</c> <c>fontcolor</c> option.
        /// </summary>
        public string Color { get; set; } = "white";

        // ── Content ───────────────────────────────────────────────────────────

        /// <summary>
        /// Static or format string for <see cref="OverlayType.Text"/> overlays.
        /// Supports FFmpeg strftime expansion when <see cref="Type"/> is
        /// <see cref="OverlayType.Timestamp"/>.
        /// Null for non-text overlays.
        /// </summary>
        public string? Text { get; set; }

        /// <summary>Diameter in pixels of the cursor dot for Cursor overlays.</summary>
        public int CursorSize { get; set; } = 24;

        /// <summary>
        /// Absolute path to a PNG/JPG image file for <see cref="OverlayType.Logo"/> overlays.
        /// Null for non-image overlays.
        /// </summary>
        public string? LogoPath { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // OverlayType
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Specifies the kind of content an <see cref="OverlayConfig"/> renders.</summary>
    public enum OverlayType
    {
        /// <summary>
        /// Live date/time stamp rendered with FFmpeg <c>drawtext</c> and
        /// <c>%{localtime}</c> / <c>pts</c> expansion.
        /// The <see cref="OverlayConfig.Text"/> field may supply a
        /// <c>strftime</c>-style format string; default is <c>"%Y-%m-%d %H:%M:%S"</c>.
        /// </summary>
        Timestamp,

        /// <summary>
        /// Static text string rendered with FFmpeg <c>drawtext</c>.
        /// Use <see cref="OverlayConfig.Text"/> for the string.
        /// </summary>
        Text,

        /// <summary>
        /// Image logo composited with FFmpeg <c>overlay</c>.
        /// Use <see cref="OverlayConfig.LogoPath"/> to specify the image file.
        /// </summary>
        Logo,

        /// <summary>
        /// Camera name label (equivalent to <see cref="Text"/> pre-filled with
        /// <see cref="CameraConfig.Name"/>).
        /// </summary>
        CameraName,

        /// <summary>
        /// Live mouse-cursor dot composited onto the video.
        /// Uses <see cref="OverlayConfig.CursorSize"/> and <see cref="OverlayConfig.Color"/>.
        /// </summary>
        Cursor,
    }
}
