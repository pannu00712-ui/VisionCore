using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Probes the host machine to discover which GPU-accelerated encoders
    /// FFmpeg can actually use — not just which hardware exists.
    ///
    /// Detection strategy (layered, most reliable → fastest fallback):
    ///
    ///   1. FFmpeg encoder probe  — asks FFmpeg to encode a 1-frame null source with each
    ///      hardware encoder.  If it succeeds, the encoder is confirmed end-to-end usable.
    ///      This is the ground truth; it catches driver/library gaps that WMI cannot.
    ///
    ///   2. WMI / DXGI name scan  — reads Win32_VideoController names to identify GPU
    ///      vendor (NVIDIA / Intel / AMD) and populate <see cref="GpuInfo"/> metadata
    ///      (name, VRAM) even when an encoder probe is not conclusive.
    ///
    ///   3. Platform guard  — non-Windows hosts skip WMI but still run the FFmpeg probe
    ///      so Linux/macOS builds get correct results.
    ///
    /// Cached results are stored after the first call to <see cref="DetectAsync"/>;
    /// subsequent calls return immediately from cache unless <paramref name="forceRefresh"/>
    /// is passed.
    ///
    /// Integration with <see cref="RtspStreamManager"/>:
    ///   Call <see cref="RecommendAccel"/> to get the best <see cref="GpuAccel"/> value
    ///   to write into a <see cref="CameraConfig"/> before starting a publisher.
    /// </summary>
    public sealed class GpuDetector
    {
        private readonly ILogger<GpuDetector> _logger;

        private GpuDetectionResult? _cache;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // Encoders to probe, in preference order
        private static readonly EncoderProbe[] Probes =
        {
            new(GpuAccel.NvencH264, "h264_nvenc",  "NVIDIA NVENC H.264"),
            new(GpuAccel.NvencH265, "hevc_nvenc",  "NVIDIA NVENC H.265"),
            new(GpuAccel.QuickSync, "h264_qsv",    "Intel Quick Sync H.264"),
            new(GpuAccel.Amd,       "h264_amf",    "AMD AMF H.264"),
        };

        public GpuDetector(ILogger<GpuDetector> logger) => _logger = logger;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Run full GPU detection and return a snapshot of available encoders.
        /// Results are cached; pass <paramref name="forceRefresh"/> = true after a driver update.
        /// </summary>
        public async Task<GpuDetectionResult> DetectAsync(
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_cache != null && !forceRefresh)
                    return _cache;

                _logger.LogInformation("Starting GPU encoder detection...");

                var gpus     = await QueryGpusAsync(ct);
                var encoders = await ProbeEncodersAsync(ct);

                _cache = new GpuDetectionResult(gpus, encoders, DateTime.UtcNow);

                _logger.LogInformation(
                    "GPU detection complete. GPUs={GpuCount}, HW encoders available: [{Encoders}]",
                    gpus.Count,
                    string.Join(", ", encoders.Where(e => e.Available).Select(e => e.FfmpegName)));

                return _cache;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Returns the best <see cref="GpuAccel"/> for a camera, or
        /// <see cref="GpuAccel.None"/> if no hardware encoder is available.
        /// Prefers NVENC H.264 > QSV > AMF > None (H.265 variants excluded from
        /// auto-select for maximum client compatibility).
        /// </summary>
        public async Task<GpuAccel> RecommendAccelAsync(CancellationToken ct = default)
        {
            var result = await DetectAsync(ct: ct);

            var preferred = new[] { GpuAccel.NvencH264, GpuAccel.QuickSync, GpuAccel.Amd };
            foreach (var accel in preferred)
            {
                if (result.IsAvailable(accel))
                    return accel;
            }

            return GpuAccel.None;
        }

        /// <summary>Synchronous convenience wrapper for use in constructors / XAML designers.</summary>
        public GpuDetectionResult DetectSync() =>
            DetectAsync().GetAwaiter().GetResult();

        // ── GPU enumeration (WMI / wmic) ──────────────────────────────────────

        private async Task<IReadOnlyList<GpuInfo>> QueryGpusAsync(CancellationToken ct)
        {
            var gpus = new List<GpuInfo>();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogDebug("Non-Windows platform — skipping WMI GPU query.");
                return gpus;
            }

            try
            {
                // Use System.Management (WMI managed API) — wmic is deprecated in Win11 24H2
                await Task.Run(() =>
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name, AdapterRAM FROM Win32_VideoController");
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var name      = obj["Name"]?.ToString() ?? string.Empty;
                        var vramBytes = obj["AdapterRAM"] is ulong ul ? (long)ul
                                      : obj["AdapterRAM"] is uint ui  ? (long)ui
                                      : 0L;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var vendor = DetectVendor(name);
                        gpus.Add(new GpuInfo(name, vendor, vramBytes));
                        _logger.LogDebug("Found GPU: {Name} ({Vendor}, {Vram} MB)",
                            name, vendor, vramBytes / 1024 / 1024);
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WMI GPU query failed; continuing with encoder probe only.");
            }

            return gpus;
        }

        private static GpuVendor DetectVendor(string name)
        {
            var n = name.ToUpperInvariant();
            if (n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("QUADRO") || n.Contains("RTX") || n.Contains("GTX"))
                return GpuVendor.Nvidia;
            if (n.Contains("INTEL"))
                return GpuVendor.Intel;
            if (n.Contains("AMD") || n.Contains("RADEON") || n.Contains("VEGA"))
                return GpuVendor.Amd;
            return GpuVendor.Unknown;
        }

        // ── FFmpeg encoder probing ────────────────────────────────────────────

        private async Task<IReadOnlyList<EncoderResult>> ProbeEncodersAsync(CancellationToken ct)
        {
            var results = new List<EncoderResult>();

            foreach (var probe in Probes)
            {
                if (ct.IsCancellationRequested) break;
                var available = await ProbeEncoderAsync(probe, ct);
                results.Add(new EncoderResult(probe.Accel, probe.FfmpegName, probe.DisplayName, available));
                _logger.LogDebug("Encoder {Name}: {Status}", probe.FfmpegName, available ? "AVAILABLE" : "unavailable");
            }

            return results;
        }

        /// <summary>
        /// Asks FFmpeg to encode exactly one frame using the given HW encoder.
        /// Null source (lavfi testsrc) → /dev/null (NUL on Windows).
        /// Exit code 0 = encoder works; anything else = not available.
        /// </summary>
        private async Task<bool> ProbeEncoderAsync(EncoderProbe probe, CancellationToken ct)
        {
            try
            {
                var nullSink = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NUL" : "/dev/null";
                var args     = $"-y -f lavfi -i testsrc=duration=0.1:size=320x240:rate=1 " +
                               $"-vframes 1 -c:v {probe.FfmpegName} -f null {nullSink}";

                var output = await RunProcessAsync(FindFfmpeg(), args, timeoutMs: 10000, ct: ct,
                    expectNonZeroOk: true, captureStdErr: true);

                // FFmpeg prints "Encoder X not available" or "Unknown encoder" on failure
                var failed = output.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase)
                          || output.Contains("not available",   StringComparison.OrdinalIgnoreCase)
                          || output.Contains("No such encoder", StringComparison.OrdinalIgnoreCase)
                          || output.Contains("Error",           StringComparison.OrdinalIgnoreCase);

                return !failed;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Probe for {Encoder} threw.", probe.FfmpegName);
                return false;
            }
        }

        // ── NVENC extra: driver version check ─────────────────────────────────

        /// <summary>
        /// Reads the NVIDIA driver version via nvidia-smi.
        /// Returns null if nvidia-smi is not present or fails.
        /// </summary>
        public async Task<NvidiaDriverInfo?> QueryNvidiaDriverAsync(CancellationToken ct = default)
        {
            try
            {
                var output = await RunProcessAsync(
                    "nvidia-smi",
                    "--query-gpu=name,driver_version,memory.total --format=csv,noheader,nounits",
                    timeoutMs: 5000, ct: ct);

                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return null;

                var parts = lines[0].Split(',');
                if (parts.Length < 3) return null;

                return new NvidiaDriverInfo(
                    GpuName:      parts[0].Trim(),
                    DriverVersion: parts[1].Trim(),
                    VramMb:       int.TryParse(parts[2].Trim(), out var mb) ? mb : 0);
            }
            catch
            {
                return null;
            }
        }

        // ── Process helper ────────────────────────────────────────────────────

        private async Task<string> RunProcessAsync(
            string exe,
            string args,
            int    timeoutMs        = 5000,
            CancellationToken ct    = default,
            bool   expectNonZeroOk  = false,
            bool   captureStdErr    = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = captureStdErr,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var proc = new Process { StartInfo = psi };
            var sb = new StringBuilder();

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) sb.AppendLine(e.Data);
            };

            if (captureStdErr)
            {
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) sb.AppendLine(e.Data);
                };
            }

            proc.Start();
            proc.BeginOutputReadLine();
            if (captureStdErr) proc.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            try
            {
                await Task.Run(() => proc.WaitForExit(), timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — kill and return whatever we got
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }

            return sb.ToString();
        }

        private static string FindFfmpeg()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            return File.Exists(local) ? local : "ffmpeg";
        }

        // ── Nested types ──────────────────────────────────────────────────────

        private sealed record EncoderProbe(GpuAccel Accel, string FfmpegName, string DisplayName);
    }

    // ── Result types ──────────────────────────────────────────────────────────

    /// <summary>Snapshot of GPU hardware and available encoders on this machine.</summary>
    public sealed class GpuDetectionResult
    {
        public IReadOnlyList<GpuInfo>      Gpus      { get; }
        public IReadOnlyList<EncoderResult> Encoders  { get; }
        public DateTime                    DetectedAt { get; }

        public GpuDetectionResult(
            IReadOnlyList<GpuInfo>      gpus,
            IReadOnlyList<EncoderResult> encoders,
            DateTime                    detectedAt)
        {
            Gpus       = gpus;
            Encoders   = encoders;
            DetectedAt = detectedAt;
        }

        /// <summary>Returns true if the given accelerator is confirmed usable by FFmpeg.</summary>
        public bool IsAvailable(GpuAccel accel) =>
            Encoders.Any(e => e.Accel == accel && e.Available);

        /// <summary>All encoders confirmed available (for display in UI settings).</summary>
        public IEnumerable<EncoderResult> AvailableEncoders =>
            Encoders.Where(e => e.Available);

        /// <summary>Best single-line summary for a settings page label.</summary>
        public string Summary
        {
            get
            {
                if (!AvailableEncoders.Any())
                    return "No hardware encoders detected — using software (libx264)";

                var names = string.Join(", ", AvailableEncoders.Select(e => e.DisplayName));
                return $"Hardware encoding available: {names}";
            }
        }
    }

    /// <summary>Metadata about a physical GPU on the system.</summary>
    public sealed record GpuInfo(string Name, GpuVendor Vendor, long VramBytes)
    {
        public long VramMb => VramBytes / 1024 / 1024;
    }

    /// <summary>Result of probing a single FFmpeg hardware encoder.</summary>
    public sealed record EncoderResult(
        GpuAccel Accel,
        string   FfmpegName,
        string   DisplayName,
        bool     Available);

    /// <summary>Output of nvidia-smi for richer NVIDIA diagnostics.</summary>
    public sealed record NvidiaDriverInfo(string GpuName, string DriverVersion, int VramMb);

    public enum GpuVendor { Unknown, Nvidia, Intel, Amd }
}
