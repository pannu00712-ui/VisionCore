namespace VisionCore.Core.Models
{
    /// <summary>
    /// Defines a rectangular region of the Windows desktop to capture.
    ///
    /// Coordinates are in virtual-screen space (DPI-unaware pixels).
    /// A null <see cref="CameraConfig.Region"/> means capture the full primary monitor.
    ///
    /// Used by:
    ///   • <c>CameraConfig.Region</c> — persisted with the camera
    ///   • <c>RtspStreamManager</c>   — translated to FFmpeg <c>-offset_x / -offset_y / -video_size</c> args
    ///   • <c>CameraEditViewModel</c> — bound to the region-picker control
    /// </summary>
    public sealed class ScreenRegion
    {
        // ── Position ──────────────────────────────────────────────────────────

        /// <summary>
        /// Horizontal offset from the left edge of the virtual screen, in pixels.
        /// </summary>
        public int X { get; set; }

        /// <summary>
        /// Vertical offset from the top edge of the virtual screen, in pixels.
        /// </summary>
        public int Y { get; set; }

        // ── Size ──────────────────────────────────────────────────────────────

        /// <summary>Width of the capture rectangle in pixels. Must be &gt; 0.</summary>
        public int Width { get; set; } = 1920;

        /// <summary>Height of the capture rectangle in pixels. Must be &gt; 0.</summary>
        public int Height { get; set; } = 1080;

        // ── Display ───────────────────────────────────────────────────────────

        /// <summary>
        /// Zero-based monitor index this region is on.
        /// Used to resolve the correct DPI factor when mapping logical to physical pixels.
        /// 0 = primary monitor.
        /// </summary>
        public int MonitorIndex { get; set; } = 0;

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Returns <c>"X,Y WxH"</c> for display in the UI.</summary>
        public override string ToString() =>
            $"{X},{Y}  {Width}×{Height}";

        /// <summary>
        /// Returns a validated copy of this region, clamping dimensions to positive values.
        /// </summary>
        public ScreenRegion Sanitised() => new()
        {
            X            = X < 0 ? 0 : X,
            Y            = Y < 0 ? 0 : Y,
            Width        = Width  < 2 ? 2 : Width,
            Height       = Height < 2 ? 2 : Height,
            MonitorIndex = MonitorIndex < 0 ? 0 : MonitorIndex,
        };
    }
}
