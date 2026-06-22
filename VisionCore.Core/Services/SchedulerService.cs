using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Enforces schedule-based camera start/stop.
    ///
    /// Every <see cref="TickInterval"/> (default 30 s) the service evaluates
    /// each registered camera against its <see cref="ScheduleRule"/> list:
    ///
    ///   • Rules empty → no-op (camera is controlled manually / by CameraManager).
    ///   • At least one enabled rule is active → ensure the camera is running.
    ///   • No active rule → ensure the camera is stopped.
    ///
    /// CameraManager.StartCameraAsync / StopCameraAsync are called only when
    /// the desired state differs from the current state, avoiding redundant calls.
    ///
    /// Registration:
    ///   Call <see cref="Register"/> after loading cameras from SettingsService.
    ///   Call <see cref="Unregister"/> when a camera is deleted.
    ///   Settings changes are applied immediately via <see cref="UpdateRules"/>.
    /// </summary>
    public sealed class SchedulerService : IDisposable
    {
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

        private readonly ILogger<SchedulerService>  _logger;
        private readonly CameraManager              _cameraManager;
        private readonly SettingsService            _settings;

        private readonly Dictionary<Guid, ScheduledCamera> _cameras = new();
        private readonly SemaphoreSlim  _sync  = new(1, 1);
        private System.Threading.Timer?  _timer;
        private bool    _disposed;

        // ── Constructor ───────────────────────────────────────────────────────

        public SchedulerService(
            ILogger<SchedulerService> logger,
            CameraManager             cameraManager,
            SettingsService           settings)
        {
            _logger        = logger;
            _cameraManager = cameraManager;
            _settings      = settings;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public void Start()
        {
            // Seed initial state from SettingsService
            foreach (var cam in _settings.Cameras)
                Register(cam);

            _timer = new System.Threading.Timer(
                async _ => await TickAsync(),
                null,
                TimeSpan.Zero,
                TickInterval);

            _logger.LogInformation(
                "SchedulerService started. Tick every {Interval}s.", TickInterval.TotalSeconds);
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
        }

        // ── Registration ──────────────────────────────────────────────────────

        /// <summary>Register (or re-register) a camera with its schedule rules.</summary>
        public void Register(CameraConfig cam)
        {
            _cameras[cam.Id] = new ScheduledCamera
            {
                Config = cam,
                Rules  = cam.ScheduleRules ?? new List<ScheduleRule>(),
            };
        }

        /// <summary>Remove a camera from the scheduler.</summary>
        public void Unregister(Guid cameraId)
        {
            _cameras.Remove(cameraId);
        }

        /// <summary>Hot-reload schedule rules for a camera without restarting the timer.</summary>
        public void UpdateRules(Guid cameraId, List<ScheduleRule> rules)
        {
            if (_cameras.TryGetValue(cameraId, out var sc))
                sc.Rules = rules;
        }

        // ── Tick ──────────────────────────────────────────────────────────────

        private async Task TickAsync()
        {
            if (_disposed) return;
            await _sync.WaitAsync();
            try
            {
                var now = DateTime.Now; // local time for schedule rules
                foreach (var sc in _cameras.Values)
                {
                    try
                    {
                        await EvaluateCameraAsync(sc, now);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex,
                            "Scheduler tick error for camera '{Name}'.", sc.Config.Name);
                    }
                }
            }
            finally
            {
                _sync.Release();
            }
        }

        private async Task EvaluateCameraAsync(ScheduledCamera sc, DateTime now)
        {
            // No rules = not scheduler-managed
            if (sc.Rules.Count == 0) return;

            var shouldRun = IsAnyRuleActive(sc.Rules, now);
            var isRunning = _cameraManager.IsRunning(sc.Config.Id);

            if (shouldRun && !isRunning)
            {
                _logger.LogInformation(
                    "Scheduler: starting '{Name}' (schedule active).", sc.Config.Name);
                await _cameraManager.StartCameraAsync(sc.Config.Id);
            }
            else if (!shouldRun && isRunning)
            {
                _logger.LogInformation(
                    "Scheduler: stopping '{Name}' (outside schedule).", sc.Config.Name);
                await _cameraManager.StopCameraAsync(sc.Config.Id);
            }
        }

        private static bool IsAnyRuleActive(List<ScheduleRule> rules, DateTime now)
        {
            foreach (var rule in rules)
                if (rule.IsActive(now)) return true;
            return false;
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _sync.Dispose();
        }

        // ── Inner types ───────────────────────────────────────────────────────

        private sealed class ScheduledCamera
        {
            public CameraConfig      Config { get; init; } = null!;
            public List<ScheduleRule> Rules  { get; set;  } = new();
        }
    }
}
