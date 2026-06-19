using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Enumerates connected monitors and captures thumbnail screenshots.
    /// Used by the screen-picker UI to show a live preview of each monitor
    /// and let the user select a region to stream.
    /// </summary>
    public static class ScreenCaptureService
    {
        // ── Monitor info ──────────────────────────────────────────────────────

        public sealed class MonitorInfo
        {
            public int    Index       { get; init; }
            public string Name        { get; init; } = string.Empty;
            public int    X           { get; init; }
            public int    Y           { get; init; }
            public int    Width       { get; init; }
            public int    Height      { get; init; }
            public bool   IsPrimary   { get; init; }

            public override string ToString() =>
                $"{Name}  ({Width}×{Height}){(IsPrimary ? "  ★ Primary" : "")}";
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Returns info about every connected monitor.</summary>
        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                list.Add(new MonitorInfo
                {
                    Index     = i,
                    Name      = s.DeviceName.TrimStart('\\').TrimStart('.').TrimStart('\\'),
                    X         = s.Bounds.X,
                    Y         = s.Bounds.Y,
                    Width     = s.Bounds.Width,
                    Height    = s.Bounds.Height,
                    IsPrimary = s.Primary,
                });
            }
            return list;
        }

        /// <summary>
        /// Captures a thumbnail screenshot of the given monitor (or full virtual desktop if index == -1).
        /// Returns PNG bytes, or null on failure.
        /// </summary>
        public static byte[]? CaptureThumbnail(int monitorIndex, int thumbWidth = 320)
        {
            try
            {
                Rectangle bounds;
                if (monitorIndex < 0)
                {
                    // Full virtual desktop
                    bounds = SystemInformation.VirtualScreen;
                }
                else
                {
                    var screens = Screen.AllScreens;
                    if (monitorIndex >= screens.Length) return null;
                    bounds = screens[monitorIndex].Bounds;
                }

                int thumbHeight = (int)(bounds.Height * (thumbWidth / (double)bounds.Width));

                using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                using var thumb = new Bitmap(bmp, thumbWidth, thumbHeight);
                using var ms = new MemoryStream();
                thumb.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Captures a specific region of the screen as a thumbnail.
        /// </summary>
        public static byte[]? CaptureRegionThumbnail(ScreenRegion region, int thumbWidth = 320)
        {
            try
            {
                if (region.Width <= 0 || region.Height <= 0) return null;
                int thumbHeight = (int)(region.Height * (thumbWidth / (double)region.Width));

                using var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(region.X, region.Y, 0, 0,
                        new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
                }

                using var thumb = new Bitmap(bmp, thumbWidth, thumbHeight);
                using var ms = new MemoryStream();
                thumb.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Converts monitor-index + optional region into a ScreenRegion that covers
        /// the full monitor (if region is null) or validates existing region coordinates.
        /// </summary>
        public static ScreenRegion FullMonitorRegion(int monitorIndex)
        {
            var screens = Screen.AllScreens;
            var screen  = monitorIndex >= 0 && monitorIndex < screens.Length
                          ? screens[monitorIndex]
                          : Screen.PrimaryScreen ?? screens[0];

            return new ScreenRegion
            {
                MonitorIndex = monitorIndex,
                X            = screen.Bounds.X,
                Y            = screen.Bounds.Y,
                Width        = screen.Bounds.Width,
                Height       = screen.Bounds.Height,
            };
        }
    }
}
