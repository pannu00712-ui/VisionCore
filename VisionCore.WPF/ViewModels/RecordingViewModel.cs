using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;

namespace VisionCore.WPF.ViewModels
{
    /// <summary>
    /// Backs the Recording Settings panel inside the Camera Edit dialog.
    ///
    /// Binds directly to a <see cref="RecordingConfig"/> owned by
    /// <see cref="CameraEditViewModel"/>; changes are saved when the parent
    /// dialog saves the camera.
    ///
    /// Also exposes:
    ///   • Live recording status from <see cref="LocalRecordingService"/>
    ///   • Browse button for the output folder
    ///   • Immediate start/stop recording toggle
    /// </summary>
    public sealed class RecordingViewModel : ViewModelBase
    {
        private readonly ILogger<RecordingViewModel>  _logger;
        private readonly LocalRecordingService        _recorder;
        private readonly LicenseService               _license;

        private CameraConfig?    _camera;
        private RecordingConfig  _config = new();

        // ── Bound to RecordingConfig ──────────────────────────────────────────

        public bool RecordingEnabled
        {
            get => _config.Enabled;
            set { _config.Enabled = value; OnPropertyChanged(); RaiseCommandStates(); }
        }

        public string OutputFolder
        {
            get => _config.OutputFolder ?? string.Empty;
            set { _config.OutputFolder = value; OnPropertyChanged(); }
        }

        public string FileNameTemplate
        {
            get => _config.FileNameTemplate;
            set { _config.FileNameTemplate = value; OnPropertyChanged(); }
        }

        public RecordingMode SelectedMode
        {
            get => _config.Mode;
            set { _config.Mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSegmented)); }
        }

        public int SegmentMinutes
        {
            get => _config.SegmentMinutes;
            set { _config.SegmentMinutes = value; OnPropertyChanged(); }
        }

        public int RetentionDays
        {
            get => _config.RetentionDays;
            set { _config.RetentionDays = value; OnPropertyChanged(); }
        }

        public long MaxDiskMb
        {
            get => _config.MaxDiskMb;
            set { _config.MaxDiskMb = value; OnPropertyChanged(); }
        }

        public RecordingTrigger SelectedTrigger
        {
            get => _config.Trigger;
            set { _config.Trigger = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsMotionTrigger)); }
        }

        public int PostMotionSeconds
        {
            get => _config.PostMotionSeconds;
            set { _config.PostMotionSeconds = value; OnPropertyChanged(); }
        }

        public bool RecordAudio
        {
            get => _config.RecordAudio;
            set { _config.RecordAudio = value; OnPropertyChanged(); }
        }

        // ── Computed / UI helpers ─────────────────────────────────────────────

        public bool IsSegmented    => _config.Mode    == RecordingMode.Segmented;
        public bool IsMotionTrigger=> _config.Trigger == RecordingTrigger.MotionOnly;

        public bool IsLicensed     => _license.CanRecord;
        public string LicenseNudge => IsLicensed ? string.Empty
            : "Recording requires VisionCore Pro. Upgrade in Settings → License.";

        // ── Live status ───────────────────────────────────────────────────────

        private bool   _isCurrentlyRecording;
        private string _statusText = "Not recording.";

        public bool   IsCurrentlyRecording
        {
            get => _isCurrentlyRecording;
            private set { Set(ref _isCurrentlyRecording, value); RaiseCommandStates(); }
        }

        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        // ── ComboBox sources ──────────────────────────────────────────────────

        public ObservableCollection<RecordingMode>    Modes    { get; } =
            new(Enum.GetValues<RecordingMode>());

        public ObservableCollection<RecordingTrigger> Triggers { get; } =
            new(Enum.GetValues<RecordingTrigger>());

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand BrowseFolderCommand { get; }
        public ICommand ToggleRecordingCommand { get; }
        public ICommand OpenFolderCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────────

        public RecordingViewModel(
            ILogger<RecordingViewModel> logger,
            LocalRecordingService       recorder,
            LicenseService              license)
        {
            _logger   = logger;
            _recorder = recorder;
            _license  = license;

            BrowseFolderCommand    = new RelayCommand(_ => BrowseFolder());
            ToggleRecordingCommand = new AsyncRelayCommand(
                async _ => await ToggleRecordingAsync(),
                _ => _camera != null && IsLicensed && RecordingEnabled);
            OpenFolderCommand = new RelayCommand(
                _ => OpenFolder(),
                _ => Directory.Exists(ResolvedOutputFolder));
        }

        // ── Initialise ────────────────────────────────────────────────────────

        /// <summary>Bind to a specific camera's recording config.</summary>
        public void Load(CameraConfig cam, RecordingConfig cfg)
        {
            _camera = cam;
            _config = cfg;

            // Refresh all bindings
            OnPropertyChanged(string.Empty);
            RefreshStatus();
        }

        /// <summary>Returns the mutated RecordingConfig for the parent to save.</summary>
        public RecordingConfig GetConfig() => _config;

        // ── Commands impl ─────────────────────────────────────────────────────

        private void BrowseFolder()
        {
            // WPF folder picker (Microsoft.WindowsAPICodePack or WinForms fallback)
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Select recording output folder",
                UseDescriptionForTitle = true,
                SelectedPath        = string.IsNullOrWhiteSpace(OutputFolder)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
                    : OutputFolder,
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                OutputFolder = dialog.SelectedPath;
        }

        private async Task ToggleRecordingAsync()
        {
            if (_camera == null) return;

            if (IsCurrentlyRecording)
            {
                await _recorder.StopAsync(_camera.Id);
                StatusText             = "Recording stopped.";
                IsCurrentlyRecording   = false;
            }
            else
            {
                try
                {
                    await _recorder.StartAsync(_camera, _config);
                    IsCurrentlyRecording = _recorder.IsRecording(_camera.Id);
                    StatusText = IsCurrentlyRecording
                        ? $"Recording → {ResolvedOutputFolder}"
                        : "Failed to start recording. Check logs.";
                }
                catch (Exception ex)
                {
                    StatusText = $"Error: {ex.Message}";
                    _logger.LogError(ex, "Failed to start recording from UI.");
                }
            }

            RaiseCommandStates();
        }

        private void OpenFolder()
        {
            var dir = ResolvedOutputFolder;
            if (Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private string ResolvedOutputFolder
            => _camera != null
                ? _config.ResolveOutputFolder(_camera.Name)
                : string.Empty;

        private void RefreshStatus()
        {
            if (_camera == null) return;
            IsCurrentlyRecording = _recorder.IsRecording(_camera.Id);
            StatusText = IsCurrentlyRecording
                ? $"Recording → {ResolvedOutputFolder}"
                : RecordingEnabled ? "Ready (not recording)." : "Recording disabled.";
        }

        private void RaiseCommandStates()
        {
            ((AsyncRelayCommand)ToggleRecordingCommand).RaiseCanExecuteChanged();
            ((RelayCommand)OpenFolderCommand).RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsLicensed));
            OnPropertyChanged(nameof(LicenseNudge));
        }
    }
}
