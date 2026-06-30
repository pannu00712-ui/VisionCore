using System;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Update;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Polls a remote manifest URL to detect whether a newer version of VisionCore
    /// is available.
    ///
    /// Lifecycle
    /// ---------
    /// Call <see cref="StartAsync"/> once on app launch (from App.xaml.cs or
    /// VisionCoreWorker).  The checker fires <see cref="UpdateAvailable"/> when a
    /// newer manifest is fetched and verified.  The WPF tray app subscribes to that
    /// event and shows the update prompt; the service host simply logs it.
    ///
    /// The checker does NOT download or apply anything — that is
    /// <see cref="UpdateDownloader"/>'s job.
    ///
    /// Snooze / mandatory logic
    /// ------------------------
    /// • If the user clicks "Remind me later", the caller stores
    ///   <see cref="SnoozedUntil"/> and the next poll skips the event until
    ///   the snooze expires or the update is flagged mandatory.
    /// • Mandatory updates suppress the snooze and re-raise the event every
    ///   poll until applied.
    ///
    /// Back-off policy
    /// ---------------
    /// Normal polling every <see cref="PollInterval"/> (default 4 h).
    /// On network error, exponential back-off capped at <see cref="MaxBackoff"/>.
    /// </summary>
    public sealed class UpdateChecker : IDisposable
    {
        // ── Configuration ─────────────────────────────────────────────────────

        /// <summary>
        /// URL of the update manifest JSON.
        /// Override via appsettings.json:  "VisionCore:UpdateManifestUrl": "..."
        /// </summary>
        public const string DefaultManifestUrl =
            "https://releases.your-org.com/visioncore/latest.json";

        private static readonly TimeSpan PollInterval = TimeSpan.FromHours(4);
        private static readonly TimeSpan MinBackoff   = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MaxBackoff   = TimeSpan.FromHours(2);

        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly ILogger<UpdateChecker> _logger;
        private readonly UpdateVerifier         _verifier;
        private readonly HttpClient             _http;
        private readonly string                 _manifestUrl;
        private readonly AppVersion             _currentVersion;

        // ── State ─────────────────────────────────────────────────────────────

        private CancellationTokenSource? _cts;
        private Task?                    _pollTask;
        private TimeSpan                 _backoff = MinBackoff;

        public DateTime?    SnoozedUntil { get; set; }
        public AppVersion   CurrentVersion => _currentVersion;
        public UpdateManifest? LastSeenManifest { get; private set; }

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>
        /// Raised on the thread-pool when a new version is confirmed available.
        /// Subscribers (WPF VM, service logger) must marshal to the correct thread.
        /// </summary>
        public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

        // ── Constructor ───────────────────────────────────────────────────────

        public UpdateChecker(
            ILogger<UpdateChecker> logger,
            UpdateVerifier         verifier,
            string?                manifestUrl     = null,
            string?                currentVersion  = null)
        {
            _logger      = logger;
            _verifier    = verifier;
            _manifestUrl = manifestUrl ?? DefaultManifestUrl;

            // Resolve current version: prefer explicit override (tests / CI),
            // then fall back to the assembly informational version attribute.
            var raw = currentVersion
                ?? Assembly.GetEntryAssembly()
                           ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                           ?.InformationalVersion
                ?? "0.0.0";

            // Strip build metadata suffix (e.g. "1.0.0+abc123")
            var clean = raw.Contains('+') ? raw[..raw.IndexOf('+')] : raw;
            _currentVersion = AppVersion.TryParse(clean, out var v) ? v! : new AppVersion(0, 0, 0);

            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"VisionCore/{_currentVersion} ({RuntimeInformation.OSDescription})");
            _http.Timeout = TimeSpan.FromSeconds(30);

            _logger.LogInformation("UpdateChecker initialised. Current version: {Version}", _currentVersion);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Start()
        {
            _cts      = new CancellationTokenSource();
            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
            _logger.LogDebug("Update checker started (manifest: {Url}, interval: {Interval}).",
                _manifestUrl, PollInterval);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_pollTask != null)
                await _pollTask.ConfigureAwait(false);
        }

        // ── Poll loop ─────────────────────────────────────────────────────────

        private async Task PollLoopAsync(CancellationToken ct)
        {
            // Check immediately on first run, then on the regular interval.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CheckOnceAsync(ct);
                    _backoff = MinBackoff; // reset on success
                    await Task.Delay(PollInterval, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Update check failed; retrying in {Backoff}.", _backoff);
                    await Task.Delay(_backoff, ct);
                    // Exponential back-off, capped at MaxBackoff
                    _backoff = _backoff * 2 > MaxBackoff ? MaxBackoff : _backoff * 2;
                }
            }
        }

        // ── Single check ─────────────────────────────────────────────────────

        /// <summary>
        /// Performs one fetch-verify-compare cycle.
        /// Safe to call directly (e.g. from a "Check now" button handler).
        /// </summary>
        public async Task<UpdateCheckResult> CheckOnceAsync(CancellationToken ct = default)
        {
            _logger.LogDebug("Fetching update manifest from {Url}.", _manifestUrl);

            string json;
            try
            {
                json = await _http.GetStringAsync(_manifestUrl, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Manifest fetch failed.");
                return UpdateCheckResult.NetworkError(ex.Message);
            }

            UpdateManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<UpdateManifest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Manifest was null after deserialization.");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Manifest JSON is malformed.");
                return UpdateCheckResult.ParseError(ex.Message);
            }

            // ── Signature verification ────────────────────────────────────────
            if (!await _verifier.VerifyManifestAsync(manifest, ct))
            {
                _logger.LogError("Manifest signature verification FAILED for version {Ver}. Update suppressed.",
                    manifest.Version);
                return UpdateCheckResult.SignatureError();
            }

            // ── OS version gate ────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(manifest.MinOsVersion))
            {
                var osVer = Environment.OSVersion.Version;
                if (Version.TryParse(manifest.MinOsVersion, out var minVer) && osVer < minVer)
                {
                    _logger.LogInformation(
                        "Update {Ver} requires OS {Min}; current OS is {Current}. Skipping.",
                        manifest.Version, manifest.MinOsVersion, osVer);
                    return UpdateCheckResult.OsIncompatible(manifest.Version);
                }
            }

            // ── Version comparison ─────────────────────────────────────────────
            if (!AppVersion.TryParse(manifest.Version, out var remoteVersion) || remoteVersion is null)
            {
                _logger.LogWarning("Cannot parse remote version '{Ver}'.", manifest.Version);
                return UpdateCheckResult.ParseError($"Unparseable version: {manifest.Version}");
            }

            LastSeenManifest = manifest;

            if (remoteVersion <= _currentVersion)
            {
                _logger.LogDebug("Already on latest version ({Ver}).", _currentVersion);
                return UpdateCheckResult.UpToDate(_currentVersion);
            }

            // ── Snooze check ───────────────────────────────────────────────────
            bool snoozed = SnoozedUntil.HasValue
                        && DateTime.UtcNow < SnoozedUntil.Value
                        && !manifest.IsMandatory;

            _logger.LogInformation(
                "Update available: {Current} → {Remote}{Mandatory}{Snoozed}.",
                _currentVersion, remoteVersion,
                manifest.IsMandatory ? " [MANDATORY]" : "",
                snoozed ? " [snoozed]" : "");

            if (!snoozed)
            {
                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(
                    manifest, _currentVersion, remoteVersion));
            }

            return UpdateCheckResult.Available(remoteVersion, manifest, snoozed);
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _cts?.Dispose();
            _http.Dispose();
        }
    }

    // ── Result type ───────────────────────────────────────────────────────────

    public sealed class UpdateCheckResult
    {
        public UpdateCheckStatus Status    { get; private init; }
        public string?           Message   { get; private init; }
        public AppVersion?       Version   { get; private init; }
        public UpdateManifest?   Manifest  { get; private init; }
        public bool              IsSnoozed { get; private init; }

        public bool IsUpdateAvailable => Status == UpdateCheckStatus.Available;

        public static UpdateCheckResult UpToDate(AppVersion v)             => new() { Status = UpdateCheckStatus.UpToDate,      Version = v };
        public static UpdateCheckResult Available(AppVersion v, UpdateManifest m, bool snoozed) =>
                                                                              new() { Status = UpdateCheckStatus.Available,     Version = v, Manifest = m, IsSnoozed = snoozed };
        public static UpdateCheckResult NetworkError(string msg)           => new() { Status = UpdateCheckStatus.NetworkError,  Message = msg };
        public static UpdateCheckResult ParseError(string msg)             => new() { Status = UpdateCheckStatus.ParseError,    Message = msg };
        public static UpdateCheckResult SignatureError()                    => new() { Status = UpdateCheckStatus.SignatureError };
        public static UpdateCheckResult OsIncompatible(string ver)         => new() { Status = UpdateCheckStatus.OsIncompatible, Message = ver };
    }

    public enum UpdateCheckStatus
    {
        UpToDate, Available, NetworkError, ParseError, SignatureError, OsIncompatible
    }

    // ── Event arg ────────────────────────────────────────────────────────────

    public sealed class UpdateAvailableEventArgs : EventArgs
    {
        public UpdateManifest  Manifest       { get; }
        public AppVersion      CurrentVersion { get; }
        public AppVersion      NewVersion     { get; }

        public UpdateAvailableEventArgs(
            UpdateManifest manifest,
            AppVersion     currentVersion,
            AppVersion     newVersion)
        {
            Manifest       = manifest;
            CurrentVersion = currentVersion;
            NewVersion     = newVersion;
        }
    }
}
