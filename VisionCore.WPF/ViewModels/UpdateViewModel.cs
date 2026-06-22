using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Services;
using VisionCore.Core.Update;

namespace VisionCore.WPF.ViewModels
{
    /// <summary>
    /// Backs the update prompt dialog and the in-progress download sheet.
    ///
    /// Lifecycle:
    ///   1. App.xaml.cs subscribes to <see cref="UpdateChecker.UpdateAvailable"/>.
    ///   2. On event: create <see cref="UpdateViewModel"/> via DI, show dialog.
    ///   3. User clicks Install → <see cref="InstallCommand"/> downloads + applies.
    ///   4. User clicks Later    → <see cref="SnoozeCommand"/> sets SnoozedUntil
    ///      on <see cref="UpdateChecker"/> and closes the dialog.
    ///      Mandatory updates suppress the snooze path.
    /// </summary>
    public sealed class UpdateViewModel : ViewModelBase, IDisposable
    {
        // ── Services ──────────────────────────────────────────────────────────
        private readonly ILogger<UpdateViewModel> _logger;
        private readonly UpdateDownloader         _downloader;
        private readonly UpdateApplier            _applier;
        private readonly UpdateChecker            _checker;

        // ── Update info (populated from UpdateAvailableEventArgs) ─────────────
        private UpdateManifest? _manifest;
        private string          _currentVersion = string.Empty;
        private string          _newVersion     = string.Empty;
        private string          _releaseNotes   = string.Empty;
        private bool            _isMandatory;

        public string NewVersion     { get => _newVersion;     set => Set(ref _newVersion,     value); }
        public string CurrentVersion { get => _currentVersion; set => Set(ref _currentVersion, value); }
        public string ReleaseNotes   { get => _releaseNotes;   set => Set(ref _releaseNotes,   value); }
        public bool   IsMandatory    { get => _isMandatory;    set => Set(ref _isMandatory,    value); }

        // ── Download progress ─────────────────────────────────────────────────
        private bool   _isDownloading;
        private bool   _isApplying;
        private int    _progressPct;
        private string _progressMessage = string.Empty;
        private string _errorMessage    = string.Empty;

        public bool   IsDownloading    { get => _isDownloading;    set { Set(ref _isDownloading,    value); RaiseCommandStates(); } }
        public bool   IsApplying       { get => _isApplying;       set { Set(ref _isApplying,       value); RaiseCommandStates(); } }
        public int    ProgressPct      { get => _progressPct;      set => Set(ref _progressPct,      value); }
        public string ProgressMessage  { get => _progressMessage;  set => Set(ref _progressMessage,  value); }
        public string ErrorMessage     { get => _errorMessage;     set => Set(ref _errorMessage,     value); }

        public bool IsIdle    => !IsDownloading && !IsApplying;
        public bool HasError  => !string.IsNullOrEmpty(ErrorMessage);
        public bool CanSnooze => IsIdle && !IsMandatory;

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand InstallCommand { get; }
        public ICommand SnoozeCommand  { get; }
        public ICommand DismissCommand { get; }

        /// <summary>Raised to close the host dialog: true = applied, false = snoozed/dismissed.</summary>
        public event EventHandler<bool>? RequestClose;

        private readonly CancellationTokenSource _cts = new();

        // ── Constructor ───────────────────────────────────────────────────────

        public UpdateViewModel(
            ILogger<UpdateViewModel> logger,
            UpdateDownloader         downloader,
            UpdateApplier            applier,
            UpdateChecker            checker)
        {
            _logger     = logger;
            _downloader = downloader;
            _applier    = applier;
            _checker    = checker;

            InstallCommand = new AsyncRelayCommand(
                async _ => await InstallAsync(),
                _ => IsIdle);

            SnoozeCommand = new RelayCommand(
                _ => Snooze(),
                _ => CanSnooze);

            DismissCommand = new RelayCommand(
                _ => RequestClose?.Invoke(this, false));
        }

        // ── Populate from event args ──────────────────────────────────────────

        public void Load(UpdateAvailableEventArgs args)
        {
            _manifest        = args.Manifest;
            CurrentVersion   = args.CurrentVersion.ToString();
            NewVersion       = args.NewVersion.ToString();
            ReleaseNotes     = args.Manifest.ReleaseNotes ?? "No release notes provided.";
            IsMandatory      = args.Manifest.IsMandatory;
            ErrorMessage     = string.Empty;
        }

        // ── Install ───────────────────────────────────────────────────────────

        private async Task InstallAsync()
        {
            if (_manifest == null) return;

            ErrorMessage = string.Empty;
            IsDownloading = true;
            ProgressPct   = 0;

            var progress = new Progress<(string msg, int pct)>(t =>
            {
                ProgressMessage = t.msg;
                ProgressPct     = t.pct;
            });

            try
            {
                // Phase 1: Download + verify + stage
                await _downloader.DownloadAndStageAsync(
                    _manifest,
                    AppVersion.Parse(CurrentVersion),
                    progress,
                    _cts.Token);

                IsDownloading = false;
                IsApplying    = true;
                ProgressMessage = "Applying update…";

                // Phase 2: Replace files + restart service + relaunch WPF
                await _applier.ApplyAsync(_downloader, _cts.Token);

                // If ApplyAsync returns (it normally restarts the process),
                // close the dialog with success.
                RequestClose?.Invoke(this, true);
            }
            catch (OperationCanceledException)
            {
                ProgressMessage = "Cancelled.";
            }
            catch (Exception ex)
            {
                ErrorMessage    = $"Update failed: {ex.Message}";
                ProgressMessage = string.Empty;
                _logger.LogError(ex, "Update installation failed.");
            }
            finally
            {
                IsDownloading = false;
                IsApplying    = false;
            }
        }

        // ── Snooze ────────────────────────────────────────────────────────────

        private void Snooze()
        {
            _checker.SnoozedUntil = DateTime.UtcNow.AddHours(24);
            _logger.LogInformation("Update snoozed for 24 hours.");
            RequestClose?.Invoke(this, false);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RaiseCommandStates()
        {
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(CanSnooze));
            ((AsyncRelayCommand)InstallCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SnoozeCommand).RaiseCanExecuteChanged();
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }

}
