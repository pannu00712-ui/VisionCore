using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;
using Application = System.Windows.Application;

namespace VisionCore.WPF.ViewModels
{
    /// <summary>
    /// Backs the Audit Log view — a read-only, chronological record of
    /// configuration changes and camera actions (who/when/what), persisted
    /// by <see cref="SettingsService.WriteAuditLogAsync"/>.
    ///
    /// Useful in office/compliance deployments to answer "who changed this
    /// camera's settings and when?" without digging through raw log files.
    /// </summary>
    public sealed class AuditLogViewModel : ViewModelBase
    {
        private readonly ILogger<AuditLogViewModel> _logger;
        private readonly SettingsService             _settings;

        private bool   _isLoading;
        private string _statusMessage = string.Empty;

        public ObservableCollection<AuditLogEntry> Entries { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            set => Set(ref _isLoading, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => Set(ref _statusMessage, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand ExportCommand  { get; }

        public AuditLogViewModel(ILogger<AuditLogViewModel> logger, SettingsService settings)
        {
            _logger   = logger;
            _settings = settings;

            RefreshCommand = new AsyncRelayCommand(async _ => await RefreshAsync());
            ExportCommand  = new AsyncRelayCommand(async _ => await ExportAsync());
        }

        /// <summary>Called once after the main window becomes visible.</summary>
        public async Task InitialiseAsync() => await RefreshAsync();

        private async Task RefreshAsync()
        {
            IsLoading = true;
            try
            {
                var entries = await _settings.GetAuditLogAsync();
                Entries.Clear();
                foreach (var e in entries)
                    Entries.Add(e);

                StatusMessage = $"{Entries.Count} entr{(Entries.Count == 1 ? "y" : "ies")}.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to load audit log: {ex.Message}";
                _logger.LogError(ex, "AuditLog: failed to load entries.");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ExportAsync()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title    = "Export audit log",
                Filter   = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"visioncore-audit-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                IsLoading = true;
                await _settings.ExportAuditLogAsync(dialog.FileName);
                StatusMessage = $"Audit log exported to '{dialog.FileName}'.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export failed: {ex.Message}";
                _logger.LogError(ex, "AuditLog: export failed.");
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
