using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using VisionCore.Core.Models;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Background watchdog that:
    ///   1. Polls each registered stream every N seconds via MediaMTX API
    ///   2. Auto-restarts any stream that died unexpectedly
    ///   3. Emits health-change events consumed by the dashboard VM
    /// </summary>
    public sealed class RtspHealthMonitor : IDisposable
    {
        private readonly ILogger<RtspHealthMonitor> _logger;
        private readonly RtspServer _rtsp;
        private readonly RtspStreamManager _manager;
        private readonly SettingsService _settings;

        private CancellationTokenSource? _cts;
        private Task? _watchdogTask;

        // cameraId → consecutive failure count
        private readonly ConcurrentDictionary<Guid, int> _failures = new();

        public event EventHandler<StreamHealthEventArgs>? HealthChanged;

        private static readonly TimeSpan PollInterval  = TimeSpan.FromSeconds(10);
        private const int MaxRetries = 3;

        public RtspHealthMonitor(
            ILogger<RtspHealthMonitor> logger,
            RtspServer rtsp,
            RtspStreamManager manager,
            SettingsService settings)
        {
            _logger   = logger;
            _rtsp     = rtsp;
            _manager  = manager;
            _settings = settings;
        }

        public void Start()
        {
            _cts          = new CancellationTokenSource();
            _watchdogTask = Task.Run(() => RunAsync(_cts.Token));
            _logger.LogInformation("RTSP health monitor started (poll every {Sec}s).",
                PollInterval.TotalSeconds);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_watchdogTask != null)
                await _watchdogTask.ConfigureAwait(false);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, ct);
                    await CheckAllStreamsAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Watchdog tick error.");
                }
            }
        }

        private async Task CheckAllStreamsAsync(CancellationToken ct)
        {
            foreach (var cam in _settings.Cameras)
            {
                if (ct.IsCancellationRequested) return;

                var clients = _rtsp.GetClientCount(cam.Id);
                var alive   = IsPublisherAlive(cam.Id);

                if (!alive)
                {
                    var failures = _failures.AddOrUpdate(cam.Id, 1, (_, v) => v + 1);
                    _logger.LogWarning(
                        "Stream '{Name}' appears dead (failure #{N}).", cam.Name, failures);

                    HealthChanged?.Invoke(this, new StreamHealthEventArgs(cam.Id, false, failures));

                    if (failures <= MaxRetries)
                    {
                        _logger.LogInformation("Auto-restarting stream '{Name}'...", cam.Name);
                        try
                        {
                            await _manager.StopPublisherAsync(cam.Id);
                            await Task.Delay(1000, ct);
                            await _manager.StartPublisherAsync(cam, ct);
                            _failures[cam.Id] = 0;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Auto-restart failed for '{Name}'.", cam.Name);
                        }
                    }
                    else
                    {
                        _logger.LogError(
                            "Stream '{Name}' exceeded max retries ({Max}). Manual intervention required.",
                            cam.Name, MaxRetries);
                    }
                }
                else
                {
                    _failures[cam.Id] = 0;
                    HealthChanged?.Invoke(this, new StreamHealthEventArgs(cam.Id, true, 0, clients));
                }
            }
        }

        private bool IsPublisherAlive(Guid cameraId)
        {
            return _manager.IsPublisherRunning(cameraId);
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _cts?.Dispose();
        }
    }

    public record StreamHealthEventArgs(
        Guid   CameraId,
        bool   IsHealthy,
        int    FailureCount,
        int    ClientCount = 0);
}
