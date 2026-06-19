using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// On first run, if mediamtx.exe is not found, this service downloads
    /// the latest release from GitHub and extracts it to {AppDir}\mediamtx\.
    ///
    /// GitHub releases page: https://github.com/bluenviron/mediamtx/releases
    /// Downloads the platform-correct zip (windows_amd64).
    /// </summary>
    public sealed class MediaMtxDownloader
    {
        private const string LatestUrl =
            "https://github.com/bluenviron/mediamtx/releases/download/v1.9.3/mediamtx_v1.9.3_windows_amd64.zip";

        private readonly ILogger<MediaMtxDownloader> _logger;

        public MediaMtxDownloader(ILogger<MediaMtxDownloader> logger) =>
            _logger = logger;

        public string TargetDir =>
            Path.Combine(AppContext.BaseDirectory, "mediamtx");

        public string BinaryPath =>
            Path.Combine(TargetDir, "mediamtx.exe");

        public bool IsInstalled => File.Exists(BinaryPath);

        public async Task EnsureInstalledAsync(
            IProgress<(string message, int percent)>? progress = null,
            CancellationToken ct = default)
        {
            if (IsInstalled)
            {
                _logger.LogDebug("MediaMTX already installed at {Path}", BinaryPath);
                return;
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogWarning("Auto-download only supported on Windows.");
                return;
            }

            Directory.CreateDirectory(TargetDir);

            progress?.Report(("Downloading MediaMTX...", 5));
            _logger.LogInformation("Downloading MediaMTX from {Url}", LatestUrl);

            using var http     = new HttpClient();
            using var response = await http.GetAsync(LatestUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total    = response.Content.Headers.ContentLength ?? -1L;
            var zipPath  = Path.Combine(Path.GetTempPath(), "mediamtx_download.zip");

            await using (var fs = File.Create(zipPath))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer    = new byte[81920];
                long received = 0;
                int  read;

                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total > 0)
                    {
                        var pct = (int)(received * 90 / total);
                        progress?.Report(($"Downloading... {received / 1024:N0} KB", 5 + pct));
                    }
                }
            }

            progress?.Report(("Extracting...", 95));
            _logger.LogInformation("Extracting MediaMTX to {Dir}", TargetDir);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    var dest = Path.Combine(TargetDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            File.Delete(zipPath);

            progress?.Report(("MediaMTX ready.", 100));
            _logger.LogInformation("MediaMTX installed successfully at {Path}", BinaryPath);
        }
    }
}
