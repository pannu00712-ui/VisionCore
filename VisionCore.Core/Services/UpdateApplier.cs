using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Update;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Applies a staged update that was prepared by <see cref="UpdateDownloader"/>.
    ///
    /// Execution model
    /// ---------------
    /// The applier runs in ONE of two contexts:
    ///
    ///   A) WPF tray app (interactive user, elevated via UAC prompt)
    ///      Called from UpdateViewModel when the user clicks "Install now".
    ///      Stops the Windows Service → replaces app + service files → restarts
    ///      service → re-launches the WPF app → exits old process.
    ///
    ///   B) Service host (non-interactive, already running as LocalSystem)
    ///      VisionCoreWorker calls <see cref="CheckAndApplyOnStartAsync"/> during
    ///      InitialiseAsync.  If a pending update is found, the service stops
    ///      itself after applying so SCM can restart it with the new binary.
    ///
    /// File replacement strategy
    /// -------------------------
    /// Running .NET binaries cannot be overwritten in-place on Windows.
    /// The applier uses the Windows MoveFileEx MOVEFILE_DELAY_UNTIL_REBOOT trick
    /// for any file that is locked, falling back to a rename-and-replace dance:
    ///
    ///   original.dll         → original.dll.old  (rename — always works even if in use)
    ///   staging\original.dll → original.dll       (move new file into place)
    ///   original.dll.old     → (deleted on next launch)
    ///
    /// For the service EXE specifically, the service must be stopped first.
    ///
    /// Rollback
    /// --------
    /// If any step fails after files have been moved, the applier attempts to
    /// restore the .old backups.  Full transactional safety is not guaranteed —
    /// for that, use an MSI upgrade (the installer handles it correctly).
    ///
    /// Elevation
    /// ---------
    /// Writing to Program Files requires elevation.  The WPF app calls
    /// <see cref="RestartElevatedToApply"/> which re-launches the app with
    /// --apply-update and ShellExecute "runas" to trigger UAC.
    /// The re-launched instance calls <see cref="ApplyAsync"/> directly.
    /// </summary>
    public sealed class UpdateApplier
    {
        private readonly ILogger<UpdateApplier> _logger;

        // SCM service name — mirrors ServiceConstants (referenced without a
        // direct assembly dependency so the Core project stays independent)
        private const string ServiceName = "VisionCoreService";

        // How long to wait for the service to stop before giving up
        private static readonly TimeSpan ServiceStopTimeout = TimeSpan.FromSeconds(30);

        public UpdateApplier(ILogger<UpdateApplier> logger) =>
            _logger = logger;

        // ── Entry points ──────────────────────────────────────────────────────

        /// <summary>
        /// Called by VisionCoreWorker.InitialiseAsync() on every service start.
        /// Applies a pending update if one exists, then returns so the service
        /// continues starting with the new binaries already in place.
        /// </summary>
        public async Task CheckAndApplyOnStartAsync(
            UpdateDownloader  downloader,
            CancellationToken ct = default)
        {
            var marker = downloader.ReadPendingMarker();
            if (marker is null) return;

            _logger.LogInformation(
                "Pending update to {Ver} found (staged {When:O}). Applying…",
                marker.Version, marker.StagedAtUtc);

            try
            {
                // In service context we are already the target process — just
                // replace the files.  Service restart is handled by SCM after
                // the worker exits (ExitCode 0 + delayed restart configured in
                // the installer's ServiceInstall step or via sc.exe failure actions).
                await ApplyFilesAsync(marker, ct);
                DeletePendingMarker(downloader);
                _logger.LogInformation("Update {Ver} applied. Service will restart via SCM.", marker.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply pending update {Ver}. Continuing with old version.", marker.Version);
                // Don't delete the marker — let the admin investigate.
            }
        }

        /// <summary>
        /// Called from the WPF tray app (already elevated) to apply a staged update.
        /// Stops the service, replaces files, restarts the service, then re-launches
        /// the WPF app.
        /// </summary>
        public async Task ApplyAsync(
            UpdateDownloader  downloader,
            CancellationToken ct = default)
        {
            var marker = downloader.ReadPendingMarker()
                ?? throw new InvalidOperationException("No pending update marker found.");

            _logger.LogInformation("Applying update {Ver} (interactive).", marker.Version);

            // 1. Stop the Windows Service so the service binary is not locked
            await StopServiceAsync(ct);

            try
            {
                // 2. Replace files
                await ApplyFilesAsync(marker, ct);

                // 3. Clean up marker
                DeletePendingMarker(downloader);

                // 4. Restart the service
                await StartServiceAsync(ct);

                _logger.LogInformation("Update {Ver} applied successfully.", marker.Version);

                // 5. Relaunch the WPF app (new binaries are now in place)
                RelaunchApp();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Apply failed for {Ver}. Attempting rollback…", marker.Version);
                await RollbackAsync(marker.StagingDir, ct);

                // Restart the service even if rollback was needed
                await TryStartServiceAsync();
                throw;
            }
        }

        /// <summary>
        /// Relaunches the current process via ShellExecute "runas" (UAC elevation)
        /// with the --apply-update flag so the new process calls <see cref="ApplyAsync"/>.
        /// The calling process should exit after this returns.
        /// </summary>
        public static void RestartElevatedToApply()
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot determine current executable path.");

            Process.Start(new ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = "--apply-update",
                UseShellExecute = true,
                Verb            = "runas",   // triggers UAC prompt
            });
        }

        // ── Core file replacement ─────────────────────────────────────────────

        private async Task ApplyFilesAsync(PendingUpdateMarker marker, CancellationToken ct)
        {
            if (!Directory.Exists(marker.StagingDir))
                throw new DirectoryNotFoundException(
                    $"Staging directory not found: {marker.StagingDir}");

            // The staging ZIP is expected to contain two subdirectories:
            //   staging\app\      → replaces {InstallDir}\app\
            //   staging\service\  → replaces {InstallDir}\service\
            // If the ZIP is flat (all files at root), both source paths
            // fall back to the staging root.

            var installRoot = GetInstallDir();
            _logger.LogDebug("Install root resolved to: {Root}", installRoot);

            var appSrc     = Directory.Exists(Path.Combine(marker.StagingDir, "app"))
                             ? Path.Combine(marker.StagingDir, "app")
                             : marker.StagingDir;

            var serviceSrc = Directory.Exists(Path.Combine(marker.StagingDir, "service"))
                             ? Path.Combine(marker.StagingDir, "service")
                             : marker.StagingDir;

            var appDest     = Path.Combine(installRoot, "app");
            var serviceDest = Path.Combine(installRoot, "service");

            await Task.Run(() =>
            {
                // Replace app files
                if (Directory.Exists(appSrc))
                    ReplaceDirectory(appSrc, appDest);

                // Replace service files — skip appsettings.json to preserve user config
                if (Directory.Exists(serviceSrc))
                    ReplaceDirectory(serviceSrc, serviceDest,
                        excludeFileName: "appsettings.json");

            }, ct);

            // Clean up .old backup files left by previous rounds
            await Task.Run(() => CleanOldFiles(installRoot), ct);
        }

        private void ReplaceDirectory(string sourceDir, string destDir, string? excludeFileName = null)
        {
            Directory.CreateDirectory(destDir);

            foreach (var srcFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, srcFile);
                var fileName = Path.GetFileName(srcFile);

                if (!string.IsNullOrEmpty(excludeFileName)
                    && string.Equals(fileName, excludeFileName, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Skipping protected file: {File}", relative);
                    continue;
                }

                var destFile = Path.Combine(destDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                ReplaceFile(srcFile, destFile);
            }
        }

        private void ReplaceFile(string src, string dest)
        {
            if (!File.Exists(dest))
            {
                File.Move(src, dest);
                _logger.LogDebug("Placed new file: {File}", dest);
                return;
            }

            var backup = dest + ".old";

            try
            {
                // Rename existing → .old (works even if the file is memory-mapped)
                File.Move(dest, backup, overwrite: true);
                // Move new file into place
                File.Move(src, dest);
                _logger.LogDebug("Replaced: {File}", dest);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not replace {File} directly; scheduling MoveFileEx on reboot.", dest);

                // Schedule replacement on next reboot as a last resort
                MoveFileOnReboot(src, dest);
            }
        }

        private void RollbackAsync_Sync(string stagingDir)
        {
            // Restore any .old files found in the install directory
            var installRoot = GetInstallDir();
            foreach (var old in Directory.EnumerateFiles(installRoot, "*.old", SearchOption.AllDirectories))
            {
                var original = old[..^4]; // strip ".old"
                try
                {
                    File.Move(old, original, overwrite: true);
                    _logger.LogInformation("Rolled back: {File}", original);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Rollback failed for {File}.", original);
                }
            }
        }

        private Task RollbackAsync(string stagingDir, CancellationToken ct) =>
            Task.Run(() => RollbackAsync_Sync(stagingDir), ct);

        private static void CleanOldFiles(string root)
        {
            foreach (var old in Directory.EnumerateFiles(root, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(old); }
                catch { /* best-effort */ }
            }
        }

        // ── Service control ───────────────────────────────────────────────────

        private async Task StopServiceAsync(CancellationToken ct)
        {
            _logger.LogInformation("Stopping service {Name}\u2026", ServiceName);
            try
            {
                var result = await RunScExeAsync("stop", ServiceName, ct);
                if (result == 0 || result == 1062) // 1062 = not started
                {
                    // Wait up to ServiceStopTimeout for the service to actually stop
                    await WaitForServiceStateAsync("STOPPED", ServiceStopTimeout, ct);
                    _logger.LogInformation("Service {Name} stopped.", ServiceName);
                }
            }
            catch (InvalidOperationException)
            {
                _logger.LogDebug("Service {Name} not found; skipping stop.", ServiceName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stop service {Name}. Files may be locked.", ServiceName);
            }
        }

        private async Task StartServiceAsync(CancellationToken ct)
        {
            _logger.LogInformation("Starting service {Name}\u2026", ServiceName);
            try
            {
                var result = await RunScExeAsync("start", ServiceName, ct);
                if (result == 0 || result == 1056) // 1056 = already running
                {
                    await WaitForServiceStateAsync("RUNNING", ServiceStopTimeout, ct);
                    _logger.LogInformation("Service {Name} started.", ServiceName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start service {Name} after update.", ServiceName);
            }
        }

        private static async Task<int> RunScExeAsync(string command, string serviceName, CancellationToken ct)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName        = "sc.exe",
                    Arguments       = $"{command} \"{serviceName}\"",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                }
            };
            proc.Start();
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode;
        }

        private static async Task WaitForServiceStateAsync(string targetState, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName               = "sc.exe",
                        Arguments              = "query VisionCoreService",
                        UseShellExecute        = false,
                        CreateNoWindow         = true,
                        RedirectStandardOutput = true,
                    }
                };
                proc.Start();
                var output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (output.Contains(targetState)) return;
                await Task.Delay(500, ct);
            }
        }

        private async Task TryStartServiceAsync()
        {
            try { await StartServiceAsync(CancellationToken.None); }
            catch { /* already logged inside */ }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string GetInstallDir()
        {
            // Prefer the registry value written by the installer
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VisionCore");
            if (key?.GetValue("InstallPath") is string regPath && Directory.Exists(regPath))
                return regPath;

            // Fall back to the directory containing the running executable
            return Path.GetDirectoryName(
                System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? AppContext.BaseDirectory)!;
        }

        private static void DeletePendingMarker(UpdateDownloader downloader)
        {
            try { File.Delete(UpdateDownloader.PendingMarkerPath); }
            catch { /* best-effort */ }
        }

        private static void RelaunchApp()
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is null) return;
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
            Environment.Exit(0);
        }

        // ── P/Invoke: MoveFileEx with MOVEFILE_DELAY_UNTIL_REBOOT ─────────────

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
        private const uint MOVEFILE_REPLACE_EXISTING   = 0x1;

        private void MoveFileOnReboot(string src, string dest)
        {
            // Replace dest with src on next reboot
            bool ok = MoveFileEx(src, dest, MOVEFILE_DELAY_UNTIL_REBOOT | MOVEFILE_REPLACE_EXISTING);
            if (!ok)
                _logger.LogError(
                    "MoveFileEx failed (error {Code}) for {Src} → {Dest}.",
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error(), src, dest);
            else
                _logger.LogInformation("Scheduled {Src} → {Dest} on next reboot.", src, dest);
        }
    }
}
