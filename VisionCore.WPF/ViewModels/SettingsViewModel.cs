using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;

namespace VisionCore.WPF.ViewModels
{
    /// <summary>
    /// Backs the Settings page / flyout.
    ///
    /// Sections:
    ///   • General    — theme, startup behaviour, tray icon
    ///   • REST API   — enable/disable, port, token reveal/regenerate, copy token, test connection
    ///   • MediaMTX   — install status, download-with-progress, binary path
    ///   • Logging    — log level selector, open log folder
    ///
    /// All fields are loaded from <see cref="SettingsService.App"/> on <see cref="InitialiseAsync"/>.
    /// Changes are not persisted until the user clicks Save (or Apply).
    /// </summary>
    public sealed class SettingsViewModel : ViewModelBase, IDisposable
    {
        // ── Services ──────────────────────────────────────────────────────────
        private readonly ILogger<SettingsViewModel> _logger;
        private readonly SettingsService            _settings;
        private readonly MediaMtxDownloader         _downloader;
        private readonly RestApiService             _api;
        private readonly UpnpPortMappingService     _upnp;

        // ── Busy state ────────────────────────────────────────────────────────
        private bool   _isBusy;
        private string _busyMessage = string.Empty;
        public  bool   IsBusy       { get => _isBusy;      set => Set(ref _isBusy,      value); }
        public  string BusyMessage  { get => _busyMessage; set => Set(ref _busyMessage, value); }

        // ── Status ────────────────────────────────────────────────────────────
        private string _statusMessage = string.Empty;
        public  string StatusMessage  { get => _statusMessage; set => Set(ref _statusMessage, value); }

        // ══════════════════════════════════════════════════════════════════════
        // General settings
        // ══════════════════════════════════════════════════════════════════════

        private string _theme             = "Dark";
        private bool   _startWithWindows;
        private bool   _runAsService;
        private bool   _minimizeToTray    = true;
        private bool   _stealthMode;

        public string Theme             { get => _theme;             set => Set(ref _theme,             value); }
        public bool   StartWithWindows  { get => _startWithWindows;  set => Set(ref _startWithWindows,  value); }
        public bool   RunAsService      { get => _runAsService;      set => Set(ref _runAsService,      value); }
        public bool   MinimizeToTray    { get => _minimizeToTray;    set => Set(ref _minimizeToTray,    value); }

        /// <summary>
        /// Stealth Mode: hide tray icon and main window on startup.
        /// Press Ctrl+Alt+D to reveal the UI.
        /// Changing this at runtime immediately calls <see cref="App.SetStealthMode"/>.
        /// </summary>
        public bool   StealthMode
        {
            get => _stealthMode;
            set
            {
                if (Set(ref _stealthMode, value))
                {
                    if (System.Windows.Application.Current is App app)
                        app.SetStealthMode(value);
                }
            }
        }

        public string[] ThemeOptions { get; } = { "Dark", "Light", "System" };

        // ══════════════════════════════════════════════════════════════════════
        // UPnP port mapping
        // ══════════════════════════════════════════════════════════════════════

        private bool   _enableUpnp;
        private string _upnpStatusText = "UPnP is disabled.";

        /// <summary>
        /// Whether VisionCore should attempt to open router port mappings (RTSP,
        /// REST API, ONVIF range) via UPnP. Default false. Takes effect on next
        /// service start/restart; status is always shown below regardless of result.
        /// </summary>
        public bool EnableUpnp { get => _enableUpnp; set => Set(ref _enableUpnp, value); }

        /// <summary>Human-readable UPnP status (Disabled / Success + mapped ports / Failed + reason).</summary>
        public string UpnpStatusText { get => _upnpStatusText; set { Set(ref _upnpStatusText, value); OnPropertyChanged(nameof(HasUpnpStatusText)); } }

        public bool HasUpnpStatusText => !string.IsNullOrEmpty(_upnpStatusText);

        // ══════════════════════════════════════════════════════════════════════
        // REST API settings
        // ══════════════════════════════════════════════════════════════════════

        private bool   _restApiEnabled = true;
        private int    _restApiPort    = 7880;
        private string _restApiToken   = string.Empty;
        private bool   _tokenVisible;
        private string _apiTestResult  = string.Empty;

        public bool   RestApiEnabled { get => _restApiEnabled; set => Set(ref _restApiEnabled, value); }
        public int    RestApiPort    { get => _restApiPort;    set => Set(ref _restApiPort,    value); }
        public string RestApiToken   { get => _restApiToken;   set => Set(ref _restApiToken,   value); }
        public bool   TokenVisible   { get => _tokenVisible;   set { Set(ref _tokenVisible, value); OnPropertyChanged(nameof(TokenMasked)); } }
        public string TokenMasked    => _tokenVisible ? _restApiToken : new string('•', Math.Min(_restApiToken.Length, 32));
        public string ApiTestResult  { get => _apiTestResult;  set => Set(ref _apiTestResult,  value); }

        /// <summary>Convenience URL shown in the UI.</summary>
        public string ApiBaseUrl => $"http://localhost:{_restApiPort}/api/v1";
        public string SwaggerUrl => $"http://localhost:{_restApiPort}/swagger";

        // ══════════════════════════════════════════════════════════════════════
        // MediaMTX installer
        // ══════════════════════════════════════════════════════════════════════

        private bool   _mediaMtxInstalled;
        private string _mediaMtxPath       = string.Empty;
        private int    _downloadProgress;
        private string _downloadMessage    = string.Empty;
        private bool   _isDownloading;

        public bool   MediaMtxInstalled  { get => _mediaMtxInstalled; set => Set(ref _mediaMtxInstalled, value); }
        public string MediaMtxPath       { get => _mediaMtxPath;      set => Set(ref _mediaMtxPath,      value); }
        public int    DownloadProgress   { get => _downloadProgress;  set => Set(ref _downloadProgress,  value); }
        public string DownloadMessage    { get => _downloadMessage;   set => Set(ref _downloadMessage,   value); }
        public bool   IsDownloading      { get => _isDownloading;     set { Set(ref _isDownloading, value); ((AsyncRelayCommand)DownloadMediaMtxCommand).RaiseCanExecuteChanged(); } }

        // ══════════════════════════════════════════════════════════════════════
        // Logging settings
        // ══════════════════════════════════════════════════════════════════════

        private string _logLevel = "Information";
        public  string LogLevel  { get => _logLevel; set => Set(ref _logLevel, value); }

        public string[] LogLevelOptions { get; } = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

        private OnvifMode _onvifMode = OnvifMode.MultipleCamera;
        private int       _onvifSharedPort = 8090;

        // ── ONVIF mode ─────────────────────────────────────────────────────────
        public OnvifMode   OnvifMode        { get => _onvifMode;       set { Set(ref _onvifMode, value); OnPropertyChanged(nameof(IsSharedPortVisible)); } }
        public int         OnvifSharedPort  { get => _onvifSharedPort; set => Set(ref _onvifSharedPort, value); }
        public OnvifMode[] OnvifModeOptions { get; } = Enum.GetValues<OnvifMode>();
        public bool        IsSharedPortVisible => OnvifMode == OnvifMode.MultipleChannel;

        // ── Computed ──────────────────────────────────────────────────────────
        public string LogFolder => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionCore", "logs");

        // ══════════════════════════════════════════════════════════════════════
        // Commands
        // ══════════════════════════════════════════════════════════════════════

        public ICommand SaveCommand              { get; }
        public ICommand DiscardCommand           { get; }
        public ICommand ToggleTokenCommand       { get; }
        public ICommand RegenerateTokenCommand   { get; }
        public ICommand CopyTokenCommand         { get; }
        public ICommand TestApiCommand           { get; }
        public ICommand DownloadMediaMtxCommand  { get; }
        public ICommand OpenLogFolderCommand     { get; }
        public ICommand OpenSwaggerCommand       { get; }
        public ICommand ExportConfigCommand      { get; }
        public ICommand ImportConfigCommand      { get; }
        public ICommand RefreshUpnpStatusCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────────

        public SettingsViewModel(
            ILogger<SettingsViewModel> logger,
            SettingsService            settings,
            MediaMtxDownloader         downloader,
            RestApiService             api,
            UpnpPortMappingService     upnp)
        {
            _logger     = logger;
            _settings   = settings;
            _downloader = downloader;
            _api        = api;
            _upnp       = upnp;

            SaveCommand            = new AsyncRelayCommand(async _ => await SaveAsync());
            DiscardCommand         = new RelayCommand(_ => LoadFromModel());
            ToggleTokenCommand     = new RelayCommand(_ => TokenVisible = !TokenVisible);
            RegenerateTokenCommand = new RelayCommand(_ => RegenerateToken());
            CopyTokenCommand       = new RelayCommand(_ => CopyToken());
            TestApiCommand         = new AsyncRelayCommand(async _ => await TestApiAsync());
            DownloadMediaMtxCommand = new AsyncRelayCommand(
                async _ => await DownloadMediaMtxAsync(),
                _ => !IsDownloading && !MediaMtxInstalled);
            OpenLogFolderCommand   = new RelayCommand(_ => OpenLogFolder());
            OpenSwaggerCommand     = new RelayCommand(_ => OpenSwagger());
            ExportConfigCommand    = new AsyncRelayCommand(async _ => await ExportConfigAsync());
            ImportConfigCommand    = new AsyncRelayCommand(async _ => await ImportConfigAsync());
            RefreshUpnpStatusCommand = new RelayCommand(_ => RefreshUpnpStatus());

            _upnp.StatusChanged += (_, _) =>
                System.Windows.Application.Current?.Dispatcher.Invoke(RefreshUpnpStatus);
            RefreshUpnpStatus();
        }

        // ── Initialise ────────────────────────────────────────────────────────

        public Task InitialiseAsync()
        {
            LoadFromModel();
            RefreshMediaMtxStatus();
            return Task.CompletedTask;
        }

        // ── Load / save ───────────────────────────────────────────────────────

        private void LoadFromModel()
        {
            var s = _settings.App;

            Theme            = s.Theme            ?? "Dark";
            StartWithWindows = s.StartWithWindows;
            RunAsService     = s.RunAsService;
            MinimizeToTray   = s.MinimizeToTray;
            _stealthMode     = s.StealthMode;   // assign backing field — no side-effect on load
            OnPropertyChanged(nameof(StealthMode));

            RestApiEnabled   = s.RestApiEnabled;
            RestApiPort      = s.RestApiPort;
            RestApiToken     = s.RestApiToken     ?? string.Empty;
            EnableUpnp       = s.EnableUpnp;

            LogLevel         = s.LogLevel         ?? "Information";
            OnvifMode        = s.OnvifMode;
            OnvifSharedPort  = s.OnvifSharedPort;

            StatusMessage    = string.Empty;
        }

        private async Task SaveAsync()
        {
            IsBusy      = true;
            BusyMessage = "Saving settings…";
            try
            {
                var s = _settings.App;

                s.Theme            = Theme;
                s.StartWithWindows = StartWithWindows;
                s.RunAsService     = RunAsService;
                s.MinimizeToTray   = MinimizeToTray;
                s.StealthMode      = StealthMode;
                s.RestApiEnabled   = RestApiEnabled;
                s.RestApiPort      = RestApiPort;
                s.RestApiToken     = RestApiToken;
                s.EnableUpnp       = EnableUpnp;
                s.LogLevel         = LogLevel;

                await _settings.SaveAppSettingsAsync(s);

                StatusMessage = "Settings saved.";
                _logger.LogInformation("App settings saved.");
                await _settings.WriteAuditLogAsync("SettingsSaved");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Save failed: {ex.Message}";
                _logger.LogError(ex, "Failed to save app settings.");
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── API token helpers ─────────────────────────────────────────────────

        private void RefreshUpnpStatus()
        {
            UpnpStatusText = _upnp.Status switch
            {
                UpnpStatus.Disabled     => "UPnP is disabled.",
                UpnpStatus.NotAttempted => "UPnP has not run yet — start (or restart) the service to apply.",
                UpnpStatus.InProgress   => "Discovering router…",
                UpnpStatus.Success      => $"✓ {_upnp.StatusMessage}",
                UpnpStatus.Failed       => $"✗ {_upnp.StatusMessage}",
                _                       => string.Empty,
            };
        }

        private void RegenerateToken()
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            RestApiToken  = System.Security.Cryptography.RandomNumberGenerator.GetString(chars, 48);
            StatusMessage = "New token generated — click Save to persist.";
        }

        private void CopyToken()
        {
            if (!string.IsNullOrEmpty(RestApiToken))
            {
                System.Windows.Clipboard.SetText(RestApiToken);
                StatusMessage = "Token copied to clipboard.";
            }
        }

        // ── API test ──────────────────────────────────────────────────────────

        private async Task TestApiAsync()
        {
            ApiTestResult = "Testing…";
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp       = await http.GetAsync($"http://localhost:{RestApiPort}/health");
                ApiTestResult  = resp.IsSuccessStatusCode
                    ? $"✔ Reachable (HTTP {(int)resp.StatusCode})"
                    : $"✘ HTTP {(int)resp.StatusCode}";
            }
            catch (Exception ex)
            {
                ApiTestResult = $"✘ {ex.Message}";
            }
        }

        // ── MediaMTX download ─────────────────────────────────────────────────

        private void RefreshMediaMtxStatus()
        {
            MediaMtxInstalled = _downloader.IsInstalled;
            MediaMtxPath      = _downloader.BinaryPath;
        }

        private async Task DownloadMediaMtxAsync()
        {
            IsDownloading   = true;
            DownloadMessage = "Starting download…";
            DownloadProgress = 0;

            var progress = new Progress<(string message, int percent)>(t =>
            {
                DownloadMessage  = t.message;
                DownloadProgress = t.percent;
            });

            try
            {
                await _downloader.EnsureInstalledAsync(progress);
                RefreshMediaMtxStatus();
                StatusMessage = "MediaMTX installed successfully.";
            }
            catch (Exception ex)
            {
                DownloadMessage  = $"Download failed: {ex.Message}";
                StatusMessage    = "MediaMTX download failed.";
                _logger.LogError(ex, "MediaMTX download failed.");
            }
            finally
            {
                IsDownloading = false;
            }
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        private void OpenLogFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(LogFolder);
                System.Diagnostics.Process.Start("explorer.exe", LogFolder);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Cannot open log folder: {ex.Message}";
            }
        }

        private void OpenSwagger()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SwaggerUrl) { UseShellExecute = true }); }
            catch { /* browser not available */ }
        }

        // ── Config export / import ──────────────────────────────────────────

        private async Task ExportConfigAsync()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "Export VisionCore configuration",
                Filter     = "VisionCore config (*.json)|*.json|All files (*.*)|*.*",
                FileName   = $"visioncore-config-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                IsBusy = true;
                BusyMessage = "Exporting configuration…";
                await _settings.ExportConfigAsync(dialog.FileName);
                StatusMessage = $"Configuration exported to '{dialog.FileName}'.";
                _logger.LogInformation("Settings: config exported to '{Path}'.", dialog.FileName);
                await _settings.WriteAuditLogAsync("ConfigExported", dialog.FileName);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export failed: {ex.Message}";
                _logger.LogError(ex, "Settings: config export failed.");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        private async Task ImportConfigAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Import VisionCore configuration",
                Filter = "VisionCore config (*.json)|*.json|All files (*.*)|*.*",
            };

            if (dialog.ShowDialog() != true) return;

            var result = System.Windows.MessageBox.Show(
                "Importing will overwrite app settings and add/update cameras from the file.\n\n" +
                "Existing cameras with the same IDs will be replaced.\n\nContinue?",
                "Import configuration",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            try
            {
                IsBusy = true;
                BusyMessage = "Importing configuration…";
                var count = await _settings.ImportConfigAsync(dialog.FileName, replaceCameras: false);
                LoadFromModel();
                StatusMessage = $"Configuration imported ({count} camera(s)). Restart the app to apply all changes.";
                _logger.LogInformation(
                    "Settings: config imported from '{Path}' ({Count} cameras).", dialog.FileName, count);
                await _settings.WriteAuditLogAsync("ConfigImported", dialog.FileName, $"{count} camera(s)");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Import failed: {ex.Message}";
                _logger.LogError(ex, "Settings: config import failed.");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose() { /* currently no subscriptions to clean up */ }
    }
}
