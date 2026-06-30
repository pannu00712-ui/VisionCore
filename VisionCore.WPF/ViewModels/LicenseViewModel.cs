using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;
using VisionCore.Core.Services;

namespace VisionCore.WPF.ViewModels
{
    /// <summary>
    /// Backs the License Activation dialog.
    ///
    /// Lifecycle:
    ///   1. Dialog opens → <see cref="InitialiseAsync"/> called → shows current license.
    ///   2. User types a key → <see cref="ActivateCommand"/> calls LicenseService.
    ///   3. Success → <see cref="LicenseActivated"/> event fires → dialog closes.
    ///   4. <see cref="DeactivateCommand"/> reverts to Free tier (Pro users only).
    /// </summary>
    public sealed class LicenseViewModel : ViewModelBase
    {
        private readonly ILogger<LicenseViewModel> _logger;
        private readonly LicenseService            _licenseService;

        // ── Bindable state ────────────────────────────────────────────────────

        private string _licenseKey     = string.Empty;
        private string _statusMessage  = string.Empty;
        private string _tierLabel      = string.Empty;
        private string _customerName   = string.Empty;
        private string _expiryLabel    = string.Empty;
        private bool   _isActivating;
        private bool   _hasError;
        private bool   _isPro;

        public string LicenseKey
        {
            get => _licenseKey;
            set { Set(ref _licenseKey, value); RaiseCommandStates(); }
        }

        public string StatusMessage  { get => _statusMessage;  set => Set(ref _statusMessage,  value); }
        public string TierLabel      { get => _tierLabel;      set => Set(ref _tierLabel,      value); }
        public string CustomerName   { get => _customerName;   set => Set(ref _customerName,   value); }
        public string ExpiryLabel    { get => _expiryLabel;    set => Set(ref _expiryLabel,    value); }
        public bool   IsActivating   { get => _isActivating;   set { Set(ref _isActivating,   value); RaiseCommandStates(); } }
        public bool   HasError       { get => _hasError;       set => Set(ref _hasError,       value); }
        public bool   IsPro          { get => _isPro;          set => Set(ref _isPro,          value); }

        public bool CanActivate   => !IsActivating && !string.IsNullOrWhiteSpace(LicenseKey);
        public bool CanDeactivate => !IsActivating && IsPro;

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand ActivateCommand   { get; }
        public ICommand DeactivateCommand { get; }
        public ICommand CancelCommand     { get; }

        /// <summary>Raised when activation succeeds; host dialog subscribes to close.</summary>
        public event EventHandler<LicenseInfo>? LicenseActivated;

        /// <summary>Raised when the user clicks Cancel.</summary>
        public event EventHandler? Cancelled;

        // ── Constructor ───────────────────────────────────────────────────────

        public LicenseViewModel(
            ILogger<LicenseViewModel> logger,
            LicenseService            licenseService)
        {
            _logger         = logger;
            _licenseService = licenseService;

            ActivateCommand   = new AsyncRelayCommand(async _ => await ActivateAsync(), _ => CanActivate);
            DeactivateCommand = new AsyncRelayCommand(async _ => await DeactivateAsync(), _ => CanDeactivate);
            CancelCommand     = new RelayCommand(_ => Cancelled?.Invoke(this, EventArgs.Empty));
        }

        // ── Initialise ────────────────────────────────────────────────────────

        public Task InitialiseAsync()
        {
            RefreshCurrentLicense();
            return Task.CompletedTask;
        }

        // ── Commands impl ─────────────────────────────────────────────────────

        private async Task ActivateAsync()
        {
            IsActivating  = true;
            HasError      = false;
            StatusMessage = "Contacting activation server…";

            try
            {
                var result = await _licenseService.ActivateAsync(LicenseKey);

                if (result.IsValid && result.License != null)
                {
                    RefreshCurrentLicense();
                    StatusMessage = $"Activated! Welcome, {result.License.CustomerName}.";
                    _logger.LogInformation("License activated via UI.");
                    LicenseActivated?.Invoke(this, result.License);
                }
                else
                {
                    HasError      = true;
                    StatusMessage = result.ErrorMessage ?? "Activation failed.";
                }
            }
            catch (Exception ex)
            {
                HasError      = true;
                StatusMessage = $"Error: {ex.Message}";
                _logger.LogError(ex, "License activation failed.");
            }
            finally
            {
                IsActivating = false;
            }
        }

        private async Task DeactivateAsync()
        {
            IsActivating  = true;
            StatusMessage = "Deactivating…";

            try
            {
                await _licenseService.DeactivateAsync();
                RefreshCurrentLicense();
                LicenseKey    = string.Empty;
                StatusMessage = "License removed. Running in Free mode.";
            }
            catch (Exception ex)
            {
                HasError      = true;
                StatusMessage = $"Deactivation failed: {ex.Message}";
            }
            finally
            {
                IsActivating = false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void RefreshCurrentLicense()
        {
            var lic = _licenseService.Current;
            TierLabel    = lic.TierLabel;
            CustomerName = lic.CustomerName;
            IsPro        = lic.IsPaid && !lic.IsExpired;

            ExpiryLabel = lic.ExpiresUtc == DateTime.MaxValue
                ? "Never expires"
                : lic.IsExpired
                    ? $"Expired on {lic.ExpiresUtc:d}"
                    : $"Expires on {lic.ExpiresUtc:d} ({lic.DaysRemaining} days remaining)";

            if (!string.IsNullOrEmpty(lic.LicenseKey))
                LicenseKey = lic.LicenseKey;

            RaiseCommandStates();
        }

        private void RaiseCommandStates()
        {
            OnPropertyChanged(nameof(CanActivate));
            OnPropertyChanged(nameof(CanDeactivate));
            ((AsyncRelayCommand)ActivateCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)DeactivateCommand).RaiseCanExecuteChanged();
        }
    }
}
