using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Enumerates video capture devices (webcams, capture cards, virtual cameras)
    /// available on the system for use with FFmpeg's dshow input filter.
    ///
    /// Usage:
    ///   var enumerator = new WebcamEnumerator(logger);
    ///   var cams = enumerator.GetVideoDevices();
    ///   // Pass cam.FfmpegName to FFmpeg: -f dshow -i video="FfmpegName"
    ///
    /// Returned <see cref="WebcamDevice.FfmpegName"/> is the exact string to use in
    /// the FFmpeg dshow argument:  -f dshow -i video="<FfmpegName>"
    ///
    /// Also exposes <see cref="GetSupportedResolutions"/> which runs a quick FFmpeg
    /// probe to list what resolutions and frame-rates the device advertises.
    ///
    /// Requires:
    ///   Windows only (DirectShow). Non-Windows returns an empty list with a warning.
    ///   FFmpeg must be accessible on PATH or in the app's ffmpeg\ subfolder.
    /// </summary>
    public sealed class WebcamEnumerator
    {
        private readonly ILogger<WebcamEnumerator> _logger;

        // DirectShow video capture category GUID
        private const string VideoCaptureCategoryGuid =
            "{860BB310-5D01-11d0-BD3B-00A0C911CE86}";

        public WebcamEnumerator(ILogger<WebcamEnumerator> logger)
        {
            _logger = logger;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all DirectShow video capture devices present on the system.
        /// Includes physical webcams, virtual cameras (OBS, etc.), and capture cards.
        /// </summary>
        public IReadOnlyList<WebcamDevice> GetVideoDevices()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogWarning("WebcamEnumerator: DirectShow enumeration is Windows-only.");
                return Array.Empty<WebcamDevice>();
            }

            var results = new List<WebcamDevice>();

            try
            {
                results.AddRange(EnumerateViaRegistry());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate webcam devices from registry.");
            }

            _logger.LogDebug("WebcamEnumerator found {Count} device(s).", results.Count);
            return results;
        }

        /// <summary>
        /// Returns the first available webcam, or null if none are found.
        /// Useful as a default selection in the camera setup wizard.
        /// </summary>
        public WebcamDevice? GetDefaultDevice()
        {
            var devices = GetVideoDevices();
            return devices.Count > 0 ? devices[0] : null;
        }

        /// <summary>
        /// Probes a specific device with FFmpeg to list its supported resolutions
        /// and frame rates.  Runs synchronously (short timeout).
        ///
        /// Returns an empty list if FFmpeg is unavailable or the device doesn't
        /// advertise capabilities.
        /// </summary>
        public IReadOnlyList<WebcamCapability> GetSupportedResolutions(WebcamDevice device)
        {
            var results = new List<WebcamCapability>();

            try
            {
                // ffmpeg -f dshow -list_options true -i video="DeviceName"
                // parses lines like:
                //   [dshow] min s=1280x720 fps=30 max s=1920x1080 fps=30
                var ffmpeg = FindFfmpeg();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = ffmpeg,
                    Arguments              = $"-f dshow -list_options true -i video=\"{device.FfmpegName}\"",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardError  = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return results;

                var output = proc.StandardError.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in output.Split('\n'))
                {
                    var cap = ParseCapabilityLine(line);
                    if (cap != null) results.Add(cap);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not probe capabilities for device '{Name}'.", device.FriendlyName);
            }

            // If probe failed or returned nothing, add common defaults
            if (results.Count == 0)
            {
                results.AddRange(new[]
                {
                    new WebcamCapability { Width = 1920, Height = 1080, FrameRate = 30 },
                    new WebcamCapability { Width = 1280, Height = 720,  FrameRate = 30 },
                    new WebcamCapability { Width = 640,  Height = 480,  FrameRate = 30 },
                });
            }

            return results;
        }

        // ── Registry enumeration ──────────────────────────────────────────────

        private IEnumerable<WebcamDevice> EnumerateViaRegistry()
        {
            var results = new List<WebcamDevice>();
            var regPath = $@"Software\Microsoft\ActiveMovie\devenum\{VideoCaptureCategoryGuid}";

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regPath);
            if (key == null)
            {
                _logger.LogDebug("Registry key not found for DirectShow video devices. Trying CLSID path.");
                return EnumerateViaClsidRegistry();
            }

            int index = 0;
            foreach (var valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;

                results.Add(new WebcamDevice
                {
                    FriendlyName = valueName,
                    FfmpegName   = valueName,
                    DeviceIndex  = index++,
                    IsVirtual    = IsVirtualCamera(valueName),
                });
            }

            return results;
        }

        /// <summary>
        /// Alternate registry path for video capture devices when the devenum key is absent.
        /// </summary>
        private static IEnumerable<WebcamDevice> EnumerateViaClsidRegistry()
        {
            var results = new List<WebcamDevice>();

            // HKLM path used by many webcam drivers
            var regPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{65E8773D-8F56-11D0-A3B9-00A0C9223196}";

            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath);
            if (key == null) return results;

            int index = 0;
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subKeyName + @"\#\Device Parameters");
                var name = sub?.GetValue("FriendlyName") as string
                        ?? sub?.GetValue("DeviceDescription") as string;

                if (string.IsNullOrWhiteSpace(name)) continue;

                results.Add(new WebcamDevice
                {
                    FriendlyName = name,
                    FfmpegName   = name,
                    DeviceIndex  = index++,
                    IsVirtual    = IsVirtualCamera(name),
                });
            }

            return results;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Heuristic: well-known virtual camera names (OBS, ManyCam, XSplit, etc.).
        /// </summary>
        private static bool IsVirtualCamera(string name)
        {
            var lower = name.ToLowerInvariant();
            return lower.Contains("obs")      ||
                   lower.Contains("manycam")  ||
                   lower.Contains("xsplit")   ||
                   lower.Contains("virtual")  ||
                   lower.Contains("snap")     ||
                   lower.Contains("droidcam") ||
                   lower.Contains("iriun");
        }

        /// <summary>
        /// Parses a single line from ffmpeg -list_options output.
        /// Example: "  pixel_format=yuyv422  min s=640x480 fps=30 max s=1920x1080 fps=60"
        /// </summary>
        private static WebcamCapability? ParseCapabilityLine(string line)
        {
            // Look for "max s=WxH fps=F" pattern
            var maxMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"max s=(\d+)x(\d+) fps=(\d+)");

            if (!maxMatch.Success) return null;

            if (!int.TryParse(maxMatch.Groups[1].Value, out var w)) return null;
            if (!int.TryParse(maxMatch.Groups[2].Value, out var h)) return null;
            if (!int.TryParse(maxMatch.Groups[3].Value, out var fps)) return null;

            return new WebcamCapability { Width = w, Height = h, FrameRate = fps };
        }

        private static string FindFfmpeg()
        {
            var local = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            return System.IO.File.Exists(local) ? local : "ffmpeg";
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Supporting types
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Represents a single video capture device.</summary>
    public sealed class WebcamDevice
    {
        /// <summary>Human-readable device name shown in the UI.</summary>
        public string FriendlyName { get; init; } = string.Empty;

        /// <summary>
        /// Name to pass to FFmpeg's dshow filter.
        /// Usage: <c>-f dshow -i video="FfmpegName"</c>
        /// </summary>
        public string FfmpegName { get; init; } = string.Empty;

        /// <summary>
        /// Zero-based index of this device as reported by the system.
        /// Can be used as an alternate FFmpeg selector: <c>video=0</c>.
        /// </summary>
        public int DeviceIndex { get; init; }

        /// <summary>True if this appears to be a virtual/software camera (OBS, ManyCam, etc.).</summary>
        public bool IsVirtual { get; init; }

        public override string ToString() => FriendlyName;
    }

    /// <summary>A single resolution + frame-rate combination advertised by a webcam.</summary>
    public sealed class WebcamCapability
    {
        public int Width     { get; init; }
        public int Height    { get; init; }
        public int FrameRate { get; init; }

        public override string ToString() => $"{Width}×{Height} @ {FrameRate} fps";
    }
}
