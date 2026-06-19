using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Update;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Downloads and stages a verified update package into a well-known
    /// pending directory, then writes a <c>pending-update.json</c> marker
    /// that <see cref="UpdateApplier"/> reads on the next launch (or service
    /// restart).
    ///
    /// Flow
    /// ----
    ///   1. Choose the best package (Delta preferred over Full when the delta
    ///      matches the current version).
    ///   2. Download to a temp file with streaming progress callbacks — same
    ///      pattern as <see cref="MediaMtxDownloader"/>.
    ///   3. Verify SHA-256 and ED25519 signature via <see cref="UpdateVerifier"/>.
    ///      If either fails, delete the temp file and raise an error — never stage
    ///      an unverified package.
    ///   4. Extract the ZIP into <see cref="StagingDir"/> (wiping it first so a
    ///      failed previous attempt doesn't leave stale files).
    ///   5. Write <c>pending-update.json</c> alongside the staged files.
    ///
    /// The actual binary replacement is done by <see cref="UpdateApplier"/>
    /// which runs in a separate elevated process so it can replace files that
    /// are locked by the running app / service.
    ///
    /// Resumable downloads
    /// -------------------
    /// The downloader checks for a partially-downloaded temp file and sends an
    /// HTTP Range header to resume.  The server must support Accept-Ranges;
    /// if it doesn't, the download restarts from the beginning.
    /// </summary>
    public sealed class UpdateDownloader
    {
        // ── Paths ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Root update directory.  Lives next to the install directory, not
        /// inside it, so the update applier can write here while the app runs.
        ///   %LOCALAPPDATA%\VisionCore\Updates\
        /// </summary>
        public static string UpdateRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VisionCore", "Updates");

        /// <summary>Staged (extracted) update files wait here.</summary>
        public static string StagingDir =>
            Path.Combine(UpdateRoot, "staging");

        /// <summary>Path of the pending-update marker written after staging.</summary>
        public static string PendingMarkerPath =>
            Path.Combine(UpdateRoot, "pending-update.json");

        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly ILogger<UpdateDownloader> _logger;
        private readonly UpdateVerifier            _verifier;
        private readonly HttpClient                _http;

        public UpdateDownloader(
            ILogger<UpdateDownloader> logger,
            UpdateVerifier            verifier)
        {
            _logger   = logger;
            _verifier = verifier;
            _http     = new HttpClient();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if a verified, staged update is waiting to be applied.
        /// </summary>
        public bool HasPendingUpdate => File.Exists(PendingMarkerPath);

        /// <summary>
        /// Reads the pending marker written by a previous <see cref="DownloadAndStageAsync"/> call.
        /// Returns null if no pending update exists.
        /// </summary>
        public PendingUpdateMarker? ReadPendingMarker()
        {
            if (!File.Exists(PendingMarkerPath)) return null;
            try
            {
                var json = File.ReadAllText(PendingMarkerPath);
                return JsonSerializer.Deserialize<PendingUpdateMarker>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read pending-update.json; treating as no pending update.");
                return null;
            }
        }

        /// <summary>
        /// Downloads, verifies, and stages the best available package from
        /// <paramref name="manifest"/>.
        /// Progress is reported as (message, 0–100 percent).
        /// </summary>
        public async Task DownloadAndStageAsync(
            UpdateManifest                          manifest,
            AppVersion                              currentVersion,
            IProgress<(string message, int percent)>? progress = null,
            CancellationToken                       ct = default)
        {
            Directory.CreateDirectory(UpdateRoot);

            var package = ChoosePackage(manifest, currentVersion);
            _logger.LogInformation(
                "Staging update {Ver} ({Kind} package, {Size:N0} KB).",
                manifest.Version, package.Kind, package.SizeBytes / 1024);

            // ── 1. Download ────────────────────────────────────────────────────
            var zipPath = Path.Combine(UpdateRoot, $"visioncore-{manifest.Version}.zip");
            await DownloadWithResumeAsync(package, zipPath, progress, ct);

            // ── 2. Verify ──────────────────────────────────────────────────────
            progress?.Report(("Verifying download…", 92));
            bool verified = await _verifier.VerifyPackageAsync(zipPath, package, ct);
            if (!verified)
            {
                // Wipe the bad file — never stage unverified bytes
                TryDelete(zipPath);
                throw new InvalidOperationException(
                    $"Package verification failed for version {manifest.Version}. " +
                    "The download may be corrupt or tampered with.");
            }

            _logger.LogInformation("Package signature and hash verified for {Ver}.", manifest.Version);

            // ── 3. Extract to staging ──────────────────────────────────────────
            progress?.Report(("Extracting…", 95));

            if (Directory.Exists(StagingDir))
                Directory.Delete(StagingDir, recursive: true);
            Directory.CreateDirectory(StagingDir);

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, StagingDir, overwriteFiles: true);
            TryDelete(zipPath); // reclaim disk space once extracted

            _logger.LogInformation("Update {Ver} extracted to {Dir}.", manifest.Version, StagingDir);

            // ── 4. Write marker ────────────────────────────────────────────────
            var marker = new PendingUpdateMarker
            {
                Version     = manifest.Version,
                StagingDir  = StagingDir,
                StagedAtUtc = DateTime.UtcNow,
                PackageKind = package.Kind.ToString(),
                ReleaseNotes = manifest.ReleaseNotes,
                IsMandatory  = manifest.IsMandatory,
            };

            File.WriteAllText(PendingMarkerPath,
                JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));

            progress?.Report(("Ready to install.", 100));
            _logger.LogInformation("Pending-update marker written. Apply on next restart.");
        }

        // ── Package selection ─────────────────────────────────────────────────

        private static UpdatePackage ChoosePackage(UpdateManifest manifest, AppVersion current)
        {
            // Prefer a delta that matches the exact current version
            foreach (var pkg in manifest.Packages)
            {
                if (pkg.Kind == UpdatePackageKind.Delta
                    && AppVersion.TryParse(pkg.FromVersion ?? "", out var from)
                    && from == current)
                {
                    return pkg;
                }
            }

            // Fall back to Full
            foreach (var pkg in manifest.Packages)
                if (pkg.Kind == UpdatePackageKind.Full)
                    return pkg;

            throw new InvalidOperationException(
                $"Manifest for version {manifest.Version} contains no usable package.");
        }

        // ── Resumable download ────────────────────────────────────────────────

        private async Task DownloadWithResumeAsync(
            UpdatePackage                           package,
            string                                  zipPath,
            IProgress<(string message, int percent)>? progress,
            CancellationToken                       ct)
        {
            long startByte = 0;

            if (File.Exists(zipPath))
            {
                startByte = new FileInfo(zipPath).Length;
                _logger.LogDebug("Resuming download of {Url} from byte {Start}.", package.Url, startByte);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, package.Url);
            if (startByte > 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, null);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            // 416 = server doesn't support range / already complete
            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                _logger.LogDebug("Range not satisfiable — restarting download.");
                startByte = 0;
                TryDelete(zipPath);
                using var full = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                full.EnsureSuccessStatusCode();
                await StreamToFileAsync(full, zipPath, 0, package.SizeBytes, progress, ct);
                return;
            }

            response.EnsureSuccessStatusCode();

            // If server returned 200 (ignoring Range), restart from 0
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
                startByte = 0;

            await StreamToFileAsync(response, zipPath, startByte, package.SizeBytes, progress, ct);
        }

        private static async Task StreamToFileAsync(
            HttpResponseMessage                     response,
            string                                  path,
            long                                    alreadyReceived,
            long                                    totalSize,
            IProgress<(string message, int percent)>? progress,
            CancellationToken                       ct)
        {
            await using var fs     = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            var  buffer   = new byte[81_920]; // 80 KB — same as MediaMtxDownloader
            long received = alreadyReceived;
            int  read;

            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                if (totalSize > 0)
                {
                    var pct = (int)(received * 90L / totalSize); // leave 5–100 for verify/extract
                    progress?.Report(($"Downloading… {received / 1_048_576.0:F1} / {totalSize / 1_048_576.0:F1} MB", 5 + pct));
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { /* best-effort */ }
        }
    }

    // ── Marker file ───────────────────────────────────────────────────────────

    /// <summary>
    /// Written to disk after a successful stage so the applier knows what to do
    /// on the next restart.
    /// </summary>
    public sealed class PendingUpdateMarker
    {
        public string  Version      { get; init; } = string.Empty;
        public string  StagingDir   { get; init; } = string.Empty;
        public DateTime StagedAtUtc { get; init; }
        public string  PackageKind  { get; init; } = string.Empty;
        public string  ReleaseNotes { get; init; } = string.Empty;
        public bool    IsMandatory  { get; init; }
    }
}
