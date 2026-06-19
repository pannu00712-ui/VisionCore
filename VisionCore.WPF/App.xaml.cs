using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using Serilog.Events;
using VisionCore.Core;
using VisionCore.Core.Services;
using VisionCore.Core.Models;
using VisionCore.Core.Update;
using VisionCore.WPF.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace VisionCore.WPF
{
    /// <summary>
    /// WPF application entry point.
    ///
    /// Responsibilities:
    ///   1. Build the <see cref="IHost"/> with Serilog + all VisionCore services
    ///      (via <see cref="ServiceRegistration.AddVisionCoreServices"/>).
    ///   2. Register WPF-only singletons: ViewModels, GpuDetector, UpdateChecker.
    ///   3. Run the startup sequence (MediaMTX check → RTSP → REST → cameras).
    ///   4. Show <see cref="MainWindow"/> and wire up the system-tray icon.
    ///   5. Apply the user's theme preference (Dark / Light / System).
    ///   6. Handle clean shutdown (stop all services, flush logs).
    ///   7. Subscribe to <see cref="UpdateChecker.UpdateAvailable"/> and show the
    ///      update prompt on the UI thread.
    ///   8. Manage Windows startup registry key when
    ///      <see cref="AppSettings.StartWithWindows"/> changes.
    ///
    /// Threading model:
    ///   All service start/stop is done on a background <see cref="Task"/> so the
    ///   UI thread stays responsive during boot.  Theme switching is always
    ///   marshalled back to the Dispatcher.
    /// </summary>
    public partial class App : Application
    {
        // ── Host ──────────────────────────────────────────────────────────────

        private IHost? _host;

        // ── Tray icon ─────────────────────────────────────────────────────────

        private System.Windows.Forms.NotifyIcon? _trayIcon;

        // ── Shutdown guard ────────────────────────────────────────────────────

        private readonly CancellationTokenSource _appCts = new();
        private bool _isShuttingDown;

        // ── Registry key for StartWithWindows ─────────────────────────────────

        private const string StartupRegKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegValue = "VisionCore";

        // ── Stealth / Hidden Mode ─────────────────────────────────────────────

        /// <summary>
        /// Win32 RegisterHotKey / UnregisterHotKey for the Ctrl+Alt+D global hotkey
        /// that reveals the UI when Stealth Mode is active.
        /// </summary>
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // MOD_CONTROL | MOD_ALT
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT     = 0x0001;
        private const uint VK_D        = 0x44;
        private const int  HOTKEY_ID   = 0x5701; // arbitrary unique ID

        private HwndSource? _hwndSource;
        private bool        _stealthMode;

        // ══════════════════════════════════════════════════════════════════════
        // Application.OnStartup
        // ══════════════════════════════════════════════════════════════════════

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Catch any unhandled exceptions that reach the Dispatcher
            DispatcherUnhandledException += OnDispatcherUnhandledException;

            // 1. Bootstrap Serilog early so even DI errors are captured
            BootstrapLogger();

            try
            {
                // 2. Build the Generic Host (DI + config + logging)
                _host = BuildHost();

                // 3. Load settings (db is auto-initialised in SettingsService constructor)
                var settings = _host.Services.GetRequiredService<SettingsService>();

                // 4. Apply theme before any window opens
                ApplyTheme(settings.App.Theme);

                // 5. Apply startup registry entry if the setting is on
                SyncStartWithWindows(settings.App.StartWithWindows);

                // 6. Wire the update checker
                WireUpdateChecker();

                // 7. Create and show MainWindow (via DI so it gets its VM injected)
                var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                MainWindow = mainWindow;

                // In Stealth Mode the window starts hidden; engine runs silently.
                _stealthMode = settings.App.StealthMode;
                if (!_stealthMode)
                    mainWindow.Show();

                // 8. Build tray icon (MinimizeToTray support)
                BuildTrayIcon(settings.App.MinimizeToTray, settings.App.StealthMode);

                // 9. Register Ctrl+Alt+D global hotkey for Stealth Mode reveal.
                //    We register unconditionally so the hotkey works even if stealth
                //    is toggled at runtime — it simply does nothing when not in stealth.
                RegisterStealthHotkey();

                // 9. Start the engine on a background task so the UI appears immediately
                _ = Task.Run(() => StartEngineAsync(_appCts.Token));
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal error during application startup.");
                MessageBox.Show(
                    $"VisionCore failed to start:\n\n{ex.Message}\n\nSee logs for details.",
                    "VisionCore — Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Application.OnExit
        // ══════════════════════════════════════════════════════════════════════

        protected override async void OnExit(ExitEventArgs e)
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;

            Log.Information("Application shutting down (exit code {Code}).", e.ApplicationExitCode);

            // Cancel any in-flight async work
            _appCts.Cancel();

            // Stop engine services in reverse order
            if (_host != null)
            {
                try
                {
                    await StopEngineAsync();
                    await _host.StopAsync(TimeSpan.FromSeconds(15));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Error during host shutdown — continuing.");
                }
                _host.Dispose();
            }

            // Destroy tray icon
            _trayIcon?.Dispose();

            // Unregister stealth hotkey
            if (_hwndSource != null)
            {
                UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
                _hwndSource.RemoveHook(HwndHook);
                _hwndSource = null;
            }

            await Log.CloseAndFlushAsync();
            base.OnExit(e);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Host builder
        // ══════════════════════════════════════════════════════════════════════

        private IHost BuildHost()
        {
            return Host.CreateDefaultBuilder()
                .UseSerilog((ctx, services, lc) => lc
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.Debug(outputTemplate:
                        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        path: Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "VisionCore", "logs", "wpf-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        outputTemplate:
                            "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] " +
                            "{SourceContext}: {Message:lj}{NewLine}{Exception}"))
                .ConfigureServices((ctx, services) =>
                {
                    // ── Core engine services (RTSP, ONVIF, REST, cameras…) ──
                    services.AddVisionCoreServices();

                    // ── WPF-only singletons ────────────────────────────────
                    services.AddSingleton<GpuDetector>();
                    services.AddSingleton<MotionDetector>();

                    // ── Update pipeline ────────────────────────────────────
                    services.AddSingleton<UpdateVerifier>();
                    services.AddSingleton<UpdateChecker>();
                    services.AddSingleton<UpdateDownloader>();
                    services.AddSingleton<UpdateApplier>();

                    // ── ViewModels (Transient = new instance per dialog) ───
                    services.AddSingleton<DashboardViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<LogsViewModel>();
                    services.AddSingleton<AuditLogViewModel>();
                    services.AddTransient<CameraEditViewModel>();

                    // ── Shell window ───────────────────────────────────────
                    services.AddSingleton<MainWindow>();
                })
                .Build();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Engine start / stop
        // ══════════════════════════════════════════════════════════════════════

        private async Task StartEngineAsync(CancellationToken ct)
        {
            var logger     = _host!.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<App>>();
            var downloader = _host.Services.GetRequiredService<MediaMtxDownloader>();
            var rtsp       = _host.Services.GetRequiredService<RtspServer>();
            var api        = _host.Services.GetRequiredService<RestApiService>();
            var cameras    = _host.Services.GetRequiredService<CameraManager>();
            var health     = _host.Services.GetRequiredService<RtspHealthMonitor>();
            var settings   = _host.Services.GetRequiredService<SettingsService>();

            try
            {
                // Step 1: MediaMTX binary
                logger.LogInformation("Checking MediaMTX installation…");
                await downloader.EnsureInstalledAsync(
                    progress: new Progress<(string msg, int pct)>(p =>
                        logger.LogInformation("MediaMTX: {Msg} ({Pct}%)", p.msg, p.pct)),
                    ct: ct);

                // Step 2: RTSP server
                var rtspPort = settings.App.RtspPort;
                logger.LogInformation("Starting RTSP server on port {Port}…", rtspPort);
                await rtsp.StartAsync(rtspPort, ct);

                // Step 3: REST API
                if (settings.App.RestApiEnabled)
                {
                    logger.LogInformation("Starting REST API on port {Port}…", settings.App.RestApiPort);
                    await api.StartAsync(ct);
                }

                // Step 4: Auto-start cameras
                logger.LogInformation("Auto-starting enabled cameras…");
                await cameras.StartAllAsync(ct);

                // Step 5: Health watchdog
                health.Start();

                logger.LogInformation("Engine started successfully.");
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Engine startup cancelled.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Engine startup failed.");
                Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(
                        $"Engine startup error:\n\n{ex.Message}\n\nSee logs for details.",
                        "VisionCore — Engine Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning));
            }
        }

        private async Task StopEngineAsync()
        {
            if (_host == null) return;

            var cameras = _host.Services.GetService<CameraManager>();
            var health  = _host.Services.GetService<RtspHealthMonitor>();
            var api     = _host.Services.GetService<RestApiService>();
            var rtsp    = _host.Services.GetService<RtspServer>();

            if (health != null) await TryStopAsync("health monitor",  () => health.StopAsync());
            if (cameras != null) await TryStopAsync("cameras",        () => cameras.StopAllAsync());
            if (api != null)    await TryStopAsync("REST API",         () => api.StopAsync());
            if (rtsp != null)   await TryStopAsync("RTSP server",      () => rtsp.StopAsync());
        }

        private static async Task TryStopAsync(string name, Func<Task> action)
        {
            try   { await action(); }
            catch (Exception ex) { Log.Warning(ex, "Error stopping {Name}.", name); }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Theme switching
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Swap the first MergedDictionary entry to the requested theme.
        /// Safe to call from any thread — marshals to the Dispatcher.
        /// </summary>
        public void ApplyTheme(string? theme)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ApplyTheme(theme));
                return;
            }

            var resolved = ResolveTheme(theme);

            var uri = resolved == "Light"
                ? new Uri("pack://application:,,,/VisionCore.WPF;component/Themes/LightTheme.xaml")
                : new Uri("pack://application:,,,/VisionCore.WPF;component/Themes/DarkTheme.xaml");

            var dict = new ResourceDictionary { Source = uri };

            // Replace slot 0 — the theme placeholder set in App.xaml
            var merged = Resources.MergedDictionaries;
            if (merged.Count > 0)
                merged[0] = dict;
            else
                merged.Add(dict);

            Log.Debug("Theme applied: {Theme} (requested: {Requested})", resolved, theme);
        }

        /// <summary>
        /// Resolve "System" to "Dark" or "Light" by reading the Windows registry.
        /// Falls back to "Dark" if the registry key is absent (e.g. on Server).
        /// </summary>
        private static string ResolveTheme(string? theme)
        {
            if (string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase))
                return "Light";

            if (string.Equals(theme, "System", StringComparison.OrdinalIgnoreCase))
                return IsWindowsLightTheme() ? "Light" : "Dark";

            return "Dark"; // default
        }

        private static bool IsWindowsLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var val = key?.GetValue("AppsUseLightTheme");
                return val is int i && i == 1;
            }
            catch
            {
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Tray icon
        // ══════════════════════════════════════════════════════════════════════

        private void BuildTrayIcon(bool minimizeToTray, bool stealthMode = false)
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text    = "VisionCore",
                // In stealth mode the tray icon is hidden — the engine is invisible.
                // Ctrl+Alt+D is the only way to surface the UI.
                Visible = !stealthMode,
                // Icon is loaded from an embedded resource; falls back gracefully if absent
                Icon    = LoadTrayIcon(),
            };

            // Double-click → restore window
            _trayIcon.DoubleClick += (_, _) => RestoreMainWindow();

            // Context menu
            var menu = new System.Windows.Forms.ContextMenuStrip();

            var openItem = new System.Windows.Forms.ToolStripMenuItem("Open VisionCore");
            openItem.Font = new System.Drawing.Font(openItem.Font, System.Drawing.FontStyle.Bold);
            openItem.Click += (_, _) => RestoreMainWindow();

            var exitItem  = new System.Windows.Forms.ToolStripMenuItem("Exit");
            exitItem.Click += (_, _) => ExitApplication();

            menu.Items.Add(openItem);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;

            // Wire MainWindow minimize → hide if MinimizeToTray
            if (minimizeToTray && MainWindow != null)
            {
                MainWindow.StateChanged += (_, _) =>
                {
                    if (MainWindow.WindowState == WindowState.Minimized)
                        MainWindow.Hide();
                };
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Stealth Mode — global Ctrl+Alt+D hotkey
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a global hotkey (Ctrl+Alt+D) using a hidden helper window.
        /// When fired, <see cref="HwndHook"/> calls <see cref="RestoreMainWindow"/>
        /// and makes the tray icon visible again so the user can interact normally.
        /// </summary>
        private void RegisterStealthHotkey()
        {
            // We need a real HWND to call RegisterHotKey.  Use a hidden HwndSource.
            var parameters = new HwndSourceParameters("VisionCoreHotkeyHost")
            {
                Width            = 0,
                Height           = 0,
                PositionX        = 0,
                PositionY        = 0,
                WindowStyle      = 0,      // WS_DISABLED style — invisible
                ParentWindow     = IntPtr.Zero,
            };

            _hwndSource = new HwndSource(parameters);
            _hwndSource.AddHook(HwndHook);

            bool ok = RegisterHotKey(_hwndSource.Handle, HOTKEY_ID, MOD_CONTROL | MOD_ALT, VK_D);
            if (!ok)
                Log.Warning("Stealth Mode: could not register Ctrl+Alt+D hotkey (already taken?).");
            else
                Log.Debug("Stealth Mode: Ctrl+Alt+D hotkey registered (HWND={Hwnd}).", _hwndSource.Handle);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                Log.Debug("Stealth Mode: Ctrl+Alt+D received — restoring UI.");

                // Reveal tray icon so the user can interact with it going forward
                if (_trayIcon != null)
                    _trayIcon.Visible = true;

                RestoreMainWindow();
                handled = true;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Allows other code (e.g. SettingsViewModel) to toggle stealth mode at
        /// runtime without restarting the application.
        /// </summary>
        public void SetStealthMode(bool enable)
        {
            _stealthMode = enable;
            if (_trayIcon != null)
                _trayIcon.Visible = !enable;

            if (enable)
                MainWindow?.Hide();
            else
                RestoreMainWindow();

            Log.Information("Stealth Mode {State}.", enable ? "enabled" : "disabled");
        }

        private static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
                if (File.Exists(iconPath))
                    return new System.Drawing.Icon(iconPath);
            }
            catch { /* fall through to default */ }

            // SystemIcons.Application is always available
            return System.Drawing.SystemIcons.Application;
        }

        private void RestoreMainWindow()
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (MainWindow == null) return;
                MainWindow.Show();
                MainWindow.WindowState = WindowState.Normal;
                MainWindow.Activate();
            });
        }

        private void ExitApplication()
        {
            _trayIcon!.Visible = false;
            Shutdown(0);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Update checker
        // ══════════════════════════════════════════════════════════════════════

        private void WireUpdateChecker()
        {
            var checker = _host!.Services.GetRequiredService<UpdateChecker>();
            checker.UpdateAvailable += OnUpdateAvailable;

            // Start the background polling loop
            checker.Start();
        }

        private void OnUpdateAvailable(object? sender, UpdateAvailableEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                var notes = e.Manifest.ReleaseNotes ?? "No release notes provided.";
                var mandatory = e.Manifest.IsMandatory;

                var result = MessageBox.Show(
                    $"VisionCore {e.NewVersion} is available.\n\n" +
                    $"Current version: {e.CurrentVersion}\n\n" +
                    $"Release notes:\n{notes}\n\n" +
                    (mandatory ? "⚠ This update is mandatory.\n\n" : "") +
                    "Download and install now?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    _ = Task.Run(() => DownloadAndApplyUpdateAsync(e));
                }
                else if (!mandatory)
                {
                    // Snooze for 24 hours
                    var checker = _host?.Services.GetRequiredService<UpdateChecker>();
                    if (checker != null)
                        checker.SnoozedUntil = DateTime.UtcNow.AddHours(24);
                }
            });
        }

        private async Task DownloadAndApplyUpdateAsync(UpdateAvailableEventArgs e)
        {
            try
            {
                var downloader = _host!.Services.GetRequiredService<UpdateDownloader>();
                var applier    = _host.Services.GetRequiredService<UpdateApplier>();

                // Download and stage the update package
                var progress = new Progress<(string msg, int pct)>(p =>
                    Log.Information("Update download: {Msg} ({Pct}%)", p.msg, p.pct));

                await downloader.DownloadAndStageAsync(
                    e.Manifest,
                    e.CurrentVersion,
                    progress,
                    _appCts.Token);

                // Apply — stops the service, replaces files, restarts
                await applier.ApplyAsync(downloader, _appCts.Token);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Update download/apply failed.");
                Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(
                        $"Update failed:\n\n{ex.Message}\n\nPlease try again later.",
                        "Update Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error));
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // StartWithWindows registry management
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Add or remove the VisionCore startup entry from
        /// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run</c>.
        /// </summary>
        public static void SyncStartWithWindows(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(StartupRegKey, writable: true);
                if (key == null) return;

                if (enable)
                {
                    var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;
                    key.SetValue(StartupRegValue, $"\"{exePath}\" --minimized");
                    Log.Debug("StartWithWindows: registry entry added.");
                }
                else
                {
                    key.DeleteValue(StartupRegValue, throwOnMissingValue: false);
                    Log.Debug("StartWithWindows: registry entry removed.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not update StartWithWindows registry entry.");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Unhandled exception handler
        // ══════════════════════════════════════════════════════════════════════

        private void OnDispatcherUnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled Dispatcher exception.");

            // Show a friendly message but keep running unless it's truly fatal
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nSee logs for details.",
                "VisionCore — Unexpected Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            e.Handled = true; // prevent process crash for non-fatal Dispatcher errors
        }

        // ══════════════════════════════════════════════════════════════════════
        // Early bootstrap logger (before DI host is ready)
        // ══════════════════════════════════════════════════════════════════════

        private static void BootstrapLogger()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VisionCore", "logs");

            Directory.CreateDirectory(logDir);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.Debug()
                .WriteTo.File(
                    path: Path.Combine(logDir, "wpf-bootstrap-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 3)
                .CreateBootstrapLogger();

            Log.Information("VisionCore WPF starting (PID {Pid}).", Environment.ProcessId);
        }
    }
}
