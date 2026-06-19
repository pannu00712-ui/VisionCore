using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;
using Timer = System.Threading.Timer;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace VisionCore.WPF.ViewModels
{
    // ══════════════════════════════════════════════════════════════════════════
    // CameraRowViewModel — one row in the dashboard grid
    // ══════════════════════════════════════════════════════════════════════════

    public sealed class CameraRowViewModel : ViewModelBase
    {
        // ── Identity ──────────────────────────────────────────────────────────
        public Guid   Id   { get; }
        public string Name { get; }

        // ── Stream state ──────────────────────────────────────────────────────
        private bool   _isRunning;
        private bool   _isHealthy  = true;
        private int    _failureCount;
        private string _statusText = "Idle";
        private string _rtspUrl    = string.Empty;

        public bool   IsRunning    { get => _isRunning;    set { if (Set(ref _isRunning,    value)) OnPropertyChanged(nameof(StatusText)); } }
        public bool   IsHealthy    { get => _isHealthy;    set { if (Set(ref _isHealthy,    value)) OnPropertyChanged(nameof(HealthIcon)); } }
        public int    FailureCount { get => _failureCount; set => Set(ref _failureCount, value); }
        public string RtspUrl      { get => _rtspUrl;      set => Set(ref _rtspUrl, value); }

        public string StatusText => _isRunning
            ? (_isHealthy ? "Streaming" : $"Unhealthy ({_failureCount} failures)")
            : "Idle";

        public string HealthIcon => _isHealthy ? "✔" : "⚠";

        // ── Live stats ────────────────────────────────────────────────────────
        private double _bitrateKbps;
        private double _fps;
        private int    _clients;
        private bool   _motionDetected;
        private string _uptime        = "00:00:00";
        private string _gpuEncoder    = "—";
        private double _cpuUsage;

        public double Bitrate        { get => _bitrateKbps;    set => Set(ref _bitrateKbps,    value); }
        public double Fps            { get => _fps;            set => Set(ref _fps,            value); }
        public int    Clients        { get => _clients;        set => Set(ref _clients,        value); }
        public bool   MotionDetected { get => _motionDetected; set => Set(ref _motionDetected, value); }
        public string Uptime         { get => _uptime;         set => Set(ref _uptime,         value); }
        public string GpuEncoder     { get => _gpuEncoder;     set => Set(ref _gpuEncoder,     value); }
        public double CpuUsage       { get => _cpuUsage;       set => Set(ref _cpuUsage,       value); }

        public CameraRowViewModel(Guid id, string name)
        {
            Id   = id;
            Name = name;
        }

        internal void ApplyStats(CameraStats s)
        {
            IsRunning    = s.IsRunning;
            Bitrate      = Math.Round(s.CurrentBitrateKbps, 1);
            Fps          = Math.Round(s.Fps, 1);
            Clients      = s.ActiveClients;
            MotionDetected = s.MotionDetected;
            Uptime       = s.Uptime.ToString(@"hh\:mm\:ss");
            GpuEncoder   = string.IsNullOrEmpty(s.GpuEncoder) ? "CPU" : s.GpuEncoder;
            CpuUsage     = Math.Round(s.CpuUsage, 1);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DashboardViewModel
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Drives the main dashboard window.
    ///
    /// Responsibilities:
    ///   • Populates the camera grid from <see cref="SettingsService"/>
    ///   • Ticks live stats every <see cref="StatsPollInterval"/> via a background timer
    ///   • Subscribes to <see cref="RtspHealthMonitor.HealthChanged"/> for health badges
    ///   • Subscribes to <see cref="MotionDetector.MotionDetected"/> for motion indicators
    ///   • Exposes Start / Stop / StartAll / StopAll / CopyUrl commands
    ///   • Exposes aggregate header counters (total cameras, active, total clients, total bitrate)
    /// </summary>
    public sealed class DashboardViewModel : ViewModelBase, IDisposable
    {
        // ── Services ──────────────────────────────────────────────────────────
        private readonly ILogger<DashboardViewModel> _logger;
        private readonly SettingsService             _settings;
        private readonly CameraManager               _cameras;
        private readonly RtspHealthMonitor           _health;
        private readonly MotionDetector              _motion;
        private readonly RestApiService              _api;
        private readonly RtspServer                  _rtsp;
        private readonly IServiceProvider            _sp;

        // ── State ─────────────────────────────────────────────────────────────
        private static readonly TimeSpan StatsPollInterval = TimeSpan.FromSeconds(2);
        private Timer?       _statsTimer;
        private bool         _isDisposed;

        // ── Observable collections ────────────────────────────────────────────
        public ObservableCollection<CameraRowViewModel> Cameras { get; } = new();

        // ── Header aggregates ─────────────────────────────────────────────────
        private int    _totalCameras;
        private int    _activeCameras;
        private int    _totalClients;
        private double _totalBitrateKbps;
        private string _appUptime = "00:00:00";
        private bool   _rtspServerOnline;
        private bool   _restApiOnline;

        public int    TotalCameras      { get => _totalCameras;      set => Set(ref _totalCameras,      value); }
        public int    ActiveCameras     { get => _activeCameras;     set => Set(ref _activeCameras,     value); }
        public int    TotalClients      { get => _totalClients;      set => Set(ref _totalClients,      value); }
        public double TotalBitrateKbps  { get => _totalBitrateKbps;  set => Set(ref _totalBitrateKbps,  value); }
        public string AppUptime         { get => _appUptime;         set => Set(ref _appUptime,         value); }
        public bool   RtspServerOnline  { get => _rtspServerOnline;  set => Set(ref _rtspServerOnline,  value); }
        public bool   RestApiOnline     { get => _restApiOnline;     set => Set(ref _restApiOnline,     value); }

        // ── Status bar ────────────────────────────────────────────────────────
        private string _statusMessage = "Ready";
        public  string StatusMessage  { get => _statusMessage; set => Set(ref _statusMessage, value); }

        // ── Selected row ──────────────────────────────────────────────────────
        private CameraRowViewModel? _selected;
        public  CameraRowViewModel? Selected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value))
                    RaiseAllCameraCommands();
            }
        }

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand StartCommand       { get; }
        public ICommand StopCommand        { get; }
        public ICommand StartAllCommand    { get; }
        public ICommand StopAllCommand     { get; }
        public ICommand CopyUrlCommand     { get; }
        public ICommand RefreshCommand     { get; }
        public ICommand AddCameraCommand   { get; }
        public ICommand EditCameraCommand  { get; }
        public ICommand DeleteCameraCommand { get; }
        public ICommand AddAllMonitorsCommand { get; }

        private static readonly DateTime _launchTime = DateTime.UtcNow;

        public DashboardViewModel(
            ILogger<DashboardViewModel> logger,
            SettingsService             settings,
            CameraManager               cameras,
            RtspHealthMonitor           health,
            MotionDetector              motion,
            RestApiService              api,
            RtspServer                  rtsp,
            IServiceProvider            sp)
        {
            _logger   = logger;
            _settings = settings;
            _cameras  = cameras;
            _health   = health;
            _motion   = motion;
            _api      = api;
            _rtsp     = rtsp;
            _sp       = sp;

            // ── Commands ───────────────────────────────────────────────────
            StartCommand = new AsyncRelayCommand(
                async _ => await StartSelectedAsync(),
                _ => Selected != null && !Selected.IsRunning);

            StopCommand = new AsyncRelayCommand(
                async _ => await StopSelectedAsync(),
                _ => Selected != null && Selected.IsRunning);

            StartAllCommand = new AsyncRelayCommand(async _ => await StartAllAsync());
            StopAllCommand  = new AsyncRelayCommand(async _ => await StopAllAsync());

            CopyUrlCommand = new RelayCommand(
                _ => CopyUrl(),
                _ => Selected != null && !string.IsNullOrEmpty(Selected.RtspUrl));

            RefreshCommand = new AsyncRelayCommand(async _ => await LoadCamerasAsync());

            AddCameraCommand    = new AsyncRelayCommand(async _ => await AddCameraAsync());
            EditCameraCommand   = new AsyncRelayCommand(
                async _ => await EditCameraAsync(),
                _ => Selected != null);
            DeleteCameraCommand = new AsyncRelayCommand(
                async _ => await DeleteCameraAsync(),
                _ => Selected != null && !Selected.IsRunning);
            AddAllMonitorsCommand = new AsyncRelayCommand(async _ => await AddAllMonitorsAsync());

            // ── Event subscriptions ────────────────────────────────────────
            _health.HealthChanged  += OnHealthChanged;
            _motion.MotionDetected += OnMotionDetected;
        }

        // ── Initialise (called from code-behind after DataContext is set) ─────

        public async Task InitialiseAsync()
        {
            await LoadCamerasAsync();

            _statsTimer = new Timer(
                _ => Application.Current?.Dispatcher.InvokeAsync(TickStats),
                null,
                StatsPollInterval,
                StatsPollInterval);
        }

        // ── Camera grid population ────────────────────────────────────────────

        private async Task LoadCamerasAsync()
        {
            Cameras.Clear();
            foreach (var cam in _settings.Cameras)
            {
                var row = new CameraRowViewModel(cam.Id, cam.Name)
                {
                    RtspUrl  = RtspStreamManager.GetClientUrl(cam),
                    IsRunning = _cameras.IsRunning(cam.Id),
                };
                Cameras.Add(row);
            }

            TotalCameras = Cameras.Count;
            await Task.CompletedTask; // placeholder for async settings load
            UpdateAggregates();
        }

        // ── Stats tick ────────────────────────────────────────────────────────

        private void TickStats()
        {
            if (_isDisposed) return;

            var allStats = _cameras.GetAllStats();

            foreach (var row in Cameras)
            {
                if (allStats.TryGetValue(row.Id, out var s))
                    row.ApplyStats(s);
                else
                    row.IsRunning = _cameras.IsRunning(row.Id);
            }

            UpdateAggregates();

            RtspServerOnline = _rtsp.IsListening;
            RestApiOnline    = _api.IsRunning;
            AppUptime        = (DateTime.UtcNow - _launchTime).ToString(@"hh\:mm\:ss");
        }

        private void UpdateAggregates()
        {
            ActiveCameras    = Cameras.Count(c => c.IsRunning);
            TotalClients     = Cameras.Sum(c => c.Clients);
            TotalBitrateKbps = Cameras.Sum(c => c.Bitrate);
        }

        // ── Stream control ────────────────────────────────────────────────────

        private async Task StartSelectedAsync()
        {
            if (Selected == null) return;
            var cam = _settings.Cameras.FirstOrDefault(c => c.Id == Selected.Id);
            if (cam == null) return;

            StatusMessage = $"Starting '{cam.Name}'…";
            try
            {
                await _cameras.StartCameraAsync(cam);
                Selected.IsRunning = true;
                Selected.RtspUrl   = RtspStreamManager.GetClientUrl(cam);
                StatusMessage      = $"'{cam.Name}' started.";
                _logger.LogInformation("Dashboard: started camera '{Name}'.", cam.Name);
                await _settings.WriteAuditLogAsync("CameraStarted", cam.Name);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error starting '{cam.Name}': {ex.Message}";
                _logger.LogError(ex, "Dashboard: failed to start '{Name}'.", cam.Name);
            }

            RaiseAllCameraCommands();
            UpdateAggregates();
        }

        private async Task StopSelectedAsync()
        {
            if (Selected == null) return;
            StatusMessage = $"Stopping '{Selected.Name}'…";
            try
            {
                await _cameras.StopCameraAsync(Selected.Id);
                Selected.IsRunning = false;
                StatusMessage      = $"'{Selected.Name}' stopped.";
                _logger.LogInformation("Dashboard: stopped camera '{Name}'.", Selected.Name);
                await _settings.WriteAuditLogAsync("CameraStopped", Selected.Name);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error stopping '{Selected.Name}': {ex.Message}";
                _logger.LogError(ex, "Dashboard: failed to stop '{Name}'.", Selected.Name);
            }

            RaiseAllCameraCommands();
            UpdateAggregates();
        }

        private async Task StartAllAsync()
        {
            StatusMessage = "Starting all cameras…";
            int started = 0, failed = 0;

            foreach (var cam in _settings.Cameras)
            {
                if (_cameras.IsRunning(cam.Id)) continue;
                try
                {
                    await _cameras.StartCameraAsync(cam);
                    started++;
                    var row = Cameras.FirstOrDefault(r => r.Id == cam.Id);
                    if (row != null) { row.IsRunning = true; row.RtspUrl = RtspStreamManager.GetClientUrl(cam); }
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Failed to start '{Name}'.", cam.Name);
                }
            }

            StatusMessage = failed == 0
                ? $"All cameras started ({started} streams)."
                : $"Started {started}, failed {failed}.";

            RaiseAllCameraCommands();
            UpdateAggregates();
        }

        private async Task StopAllAsync()
        {
            StatusMessage = "Stopping all cameras…";
            await _cameras.StopAllAsync();
            foreach (var row in Cameras) row.IsRunning = false;
            StatusMessage = "All cameras stopped.";
            RaiseAllCameraCommands();
            UpdateAggregates();
        }

        private void CopyUrl()
        {
            if (Selected == null || string.IsNullOrEmpty(Selected.RtspUrl)) return;
            Clipboard.SetText(Selected.RtspUrl);
            StatusMessage = $"Copied: {Selected.RtspUrl}";
        }

        // ── Health monitor events ─────────────────────────────────────────────

        private void OnHealthChanged(object? sender, StreamHealthEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var row = Cameras.FirstOrDefault(r => r.Id == e.CameraId);
                if (row == null) return;
                row.IsHealthy    = e.IsHealthy;
                row.FailureCount = e.FailureCount;
                row.Clients      = e.ClientCount;

                if (!e.IsHealthy)
                    StatusMessage = $"⚠ '{row.Name}' is unhealthy (failure #{e.FailureCount})";
            });
        }

        // ── Motion detector events ────────────────────────────────────────────

        private void OnMotionDetected(object? sender, MotionEventArgs e)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var row = Cameras.FirstOrDefault(r => r.Id == e.CameraId);
                if (row != null)
                    row.MotionDetected = e.IsMotion;
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // ── Camera CRUD ───────────────────────────────────────────────────────

        private async Task AddCameraAsync()
        {
            var vm     = _sp.GetRequiredService<CameraEditViewModel>();
            var dialog = new Views.CameraEditView(vm);
            dialog.Owner = Application.Current.MainWindow;

            if (dialog.ShowDialog() != true) return;

            var newCam = vm.SavedCamera;
            if (newCam == null) return;
            await LoadCamerasAsync();

            StatusMessage = $"Camera '{newCam.Name}' added.";
            _logger.LogInformation("Dashboard: camera '{Name}' added.", newCam.Name);
            await _settings.WriteAuditLogAsync("CameraAdded", newCam.Name,
                $"Source={newCam.Source}, Resolution={newCam.Resolution}");
        }

        /// <summary>
        /// Adds one full-screen "Screen" camera per connected monitor that
        /// doesn't already have a camera pointing at it — a quick bulk setup
        /// for multi-monitor PCs (e.g. dual-screen office workstations).
        ///
        /// Each new camera gets a unique RTSP path and ONVIF port, derived
        /// from the highest port currently in use. Cameras are added disabled
        /// (not auto-started) so the user can review/rename them first.
        /// </summary>
        private async Task AddAllMonitorsAsync()
        {
            var monitors = ScreenCaptureService.GetMonitors();
            if (monitors.Count <= 1)
            {
                StatusMessage = "Only one monitor detected — nothing to add.";
                return;
            }

            // Monitors already covered by an existing full-screen camera
            var coveredMonitors = _settings.Cameras
                .Where(c => c.Source == CameraSource.Screen && c.Region != null)
                .Select(c => c.Region!.MonitorIndex)
                .ToHashSet();

            // Also treat a camera with no Region (full primary monitor) as covering monitor 0
            if (_settings.Cameras.Any(c => c.Source == CameraSource.Screen && c.Region == null))
                coveredMonitors.Add(0);

            var nextRtspPort  = _settings.Cameras.Any() ? _settings.Cameras.Max(c => c.RtspPort)  + 1 : 8554;
            var nextOnvifPort = _settings.Cameras.Any() ? _settings.Cameras.Max(c => c.OnvifPort) + 1 : 8080;

            var added = 0;
            foreach (var mon in monitors)
            {
                if (coveredMonitors.Contains(mon.Index)) continue;

                var cam = new CameraConfig
                {
                    Name       = $"Monitor {mon.Index + 1}" + (mon.IsPrimary ? " (Primary)" : ""),
                    Source     = CameraSource.Screen,
                    Region     = ScreenCaptureService.FullMonitorRegion(mon.Index),
                    RtspPath   = $"monitor-{mon.Index + 1}",
                    RtspPort   = nextRtspPort++,
                    OnvifPort  = nextOnvifPort++,
                    Enabled    = false, // review before starting
                };

                await _settings.SaveCameraAsync(cam);
                await _settings.WriteAuditLogAsync("CameraAdded", cam.Name,
                    $"Bulk add-all-monitors (Monitor index {mon.Index})");
                added++;
            }

            if (added == 0)
            {
                StatusMessage = "All monitors already have a camera configured.";
                return;
            }

            await LoadCamerasAsync();
            StatusMessage = $"Added {added} camera(s) for {added} monitor(s). " +
                             "Review them in the list and click Start when ready.";
            _logger.LogInformation("Dashboard: added {Count} camera(s) via Add-all-monitors.", added);
        }

        private async Task EditCameraAsync()
        {
            if (Selected == null) return;
            var cam = _settings.Cameras.FirstOrDefault(c => c.Id == Selected.Id);
            if (cam == null) return;

            // Resolve a fresh VM then manually load the camera (edit mode)
            var vm = _sp.GetRequiredService<CameraEditViewModel>();
            vm.LoadFromConfig(cam);
            var dialog = new Views.CameraEditView(vm);
            dialog.Owner = Application.Current.MainWindow;

            if (dialog.ShowDialog() != true) return;

            var updated = vm.SavedCamera;
            if (updated == null) return;
            await LoadCamerasAsync();

            StatusMessage = $"Camera '{updated.Name}' updated.";
            _logger.LogInformation("Dashboard: camera '{Name}' updated.", updated.Name);
            await _settings.WriteAuditLogAsync("CameraEdited", updated.Name);
        }

        private async Task DeleteCameraAsync()
        {
            if (Selected == null) return;
            var cam = _settings.Cameras.FirstOrDefault(c => c.Id == Selected.Id);
            if (cam == null) return;

            var result = System.Windows.MessageBox.Show(
                $"Delete camera '{cam.Name}'?\n\nThis cannot be undone.",
                "Delete Camera",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes) return;

            if (Selected.IsRunning)
                await _cameras.StopCameraAsync(cam.Id);

            await _settings.DeleteCameraAsync(cam.Id);
            await LoadCamerasAsync();

            StatusMessage = $"Camera '{cam.Name}' deleted.";
            _logger.LogInformation("Dashboard: camera '{Name}' deleted.", cam.Name);
            await _settings.WriteAuditLogAsync("CameraDeleted", cam.Name);
        }

        private void RaiseAllCameraCommands()
        {
            ((AsyncRelayCommand)StartCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)StopCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CopyUrlCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)EditCameraCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)DeleteCameraCommand).RaiseCanExecuteChanged();
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _statsTimer?.Dispose();
            _health.HealthChanged  -= OnHealthChanged;
            _motion.MotionDetected -= OnMotionDetected;
        }
    }
}
