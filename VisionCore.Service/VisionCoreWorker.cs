using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using VisionCore.Core.Services;

namespace VisionCore.Service;

/// <summary>
/// The single <see cref="IHostedService"/> that owns the VisionCore engine lifecycle
/// inside the Windows Service process.
///
/// Start sequence (mirrors what the WPF App.xaml.cs does on launch):
///   1. Ensure MediaMTX binary is present — download if not.
///   2. Start <see cref="RtspServer"/> (spawns mediamtx.exe).
///   3. Start <see cref="RestApiService"/> (Kestrel / Minimal API).
///   4. Optionally auto-start all configured cameras via <see cref="CameraManager"/>.
///   5. Start <see cref="RtspHealthMonitor"/> watchdog.
///
/// Stop sequence (SCM sends SERVICE_CONTROL_STOP or host receives SIGTERM):
///   1. Stop health monitor.
///   2. Stop all camera publishers.
///   3. Stop REST API.
///   4. Stop RTSP server (kills mediamtx.exe).
///
/// The <see cref="StopAsync"/> timeout is governed by the host's
/// ShutdownTimeout (default 30 s) — increase via
///   builder.Services.Configure&lt;HostOptions&gt;(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60));
/// in Program.cs if cameras are slow to drain.
/// </summary>
internal sealed class VisionCoreWorker : BackgroundService
{
    private readonly ILogger<VisionCoreWorker>        _logger;
    private readonly IConfiguration                   _config;
    private readonly MediaMtxDownloader               _downloader;
    private readonly RtspServer                       _rtsp;
    private readonly RtspStreamManager                _streamManager;
    private readonly RtspHealthMonitor                _healthMonitor;
    private readonly RestApiService                   _api;
    private readonly CameraManager                    _cameras;
    private readonly OnvifServer                      _onvif;

    public VisionCoreWorker(
        ILogger<VisionCoreWorker> logger,
        IConfiguration            config,
        MediaMtxDownloader        downloader,
        RtspServer                rtsp,
        RtspStreamManager         streamManager,
        RtspHealthMonitor         healthMonitor,
        RestApiService            api,
        CameraManager             cameras,
        OnvifServer               onvif)
    {
        _logger        = logger;
        _config        = config;
        _downloader    = downloader;
        _rtsp          = rtsp;
        _streamManager = streamManager;
        _healthMonitor = healthMonitor;
        _api           = api;
        _cameras       = cameras;
        _onvif         = onvif;
    }

    // ── IHostedService.StartAsync → called by the host, not directly ─────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // BackgroundService.StartAsync waits until ExecuteAsync returns the
        // first await, so do all async init here before the long-running loop.

        try
        {
            await InitialiseAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogCritical(ex, "Fatal error during service initialisation. Service will stop.");
            // Signal the host to shut down the whole process.
            throw;
        }

        _logger.LogInformation("VisionCore Worker is running. Press Ctrl+C to stop.");

        // Keep the worker alive — all real work happens inside the services
        // started during InitialiseAsync.  We just wait for cancellation here.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal stop path — no action needed here; StopAsync handles teardown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("VisionCore Worker is stopping…");
        await TeardownAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("VisionCore Worker stopped.");
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private async Task InitialiseAsync(CancellationToken ct)
    {
        // 1. MediaMTX binary
        _logger.LogInformation("Checking MediaMTX installation…");
        await _downloader.EnsureInstalledAsync(
            progress: new Progress<(string msg, int pct)>(p =>
                _logger.LogInformation("MediaMTX: {Msg} ({Pct}%)", p.msg, p.pct)),
            ct: ct);

        // 2. RTSP server (mediamtx.exe)
        var rtspPort = _config.GetValue<int>("VisionCore:RtspPort", 8554);
        _logger.LogInformation("Starting RTSP server on port {Port}…", rtspPort);
        await _rtsp.StartAsync(rtspPort, ct);

        // 3. ONVIF server (WS-Discovery + per-camera HTTP)
        _logger.LogInformation("Starting ONVIF server…");
        await _onvif.StartAsync(ct);

        // 4. REST API (Kestrel)
        _logger.LogInformation("Starting REST API…");
        await _api.StartAsync(ct);

        // 5. Auto-start cameras
        if (_config.GetValue<bool>("VisionCore:AutoStartCameras", true))
        {
            _logger.LogInformation("Auto-starting configured cameras…");
            await AutoStartCamerasAsync(ct);
        }
        else
        {
            _logger.LogInformation("AutoStartCameras = false; cameras will start on demand via REST API.");
        }

        // 6. Health watchdog
        _healthMonitor.Start();
        _logger.LogInformation("Service initialisation complete.");
    }

    private async Task TeardownAsync(CancellationToken ct)
    {
        // Reverse of start order.  Each step is individually guarded so a
        // failure in one step does not prevent the others from running.

        await TryAsync("stop health monitor",    () => { _healthMonitor.StopAsync(); return Task.CompletedTask; });
        await TryAsync("stop all cameras",       () => _cameras.StopAllAsync());
        await TryAsync("stop ONVIF server",        () => _onvif.StopAsync());
        await TryAsync("stop REST API",          () => _api.StopAsync());
        await TryAsync("stop RTSP server",       () => _rtsp.StopAsync());
    }

    private async Task AutoStartCamerasAsync(CancellationToken ct)
    {
        // CameraManager.StartAllAsync starts every enabled camera in the
        // settings database and waits until each publisher is live.
        try
        {
            await _cameras.StartAllAsync(ct);
        }
        catch (Exception ex)
        {
            // Non-fatal: individual cameras may fail (e.g. device unplugged).
            // The health monitor will retry them.
            _logger.LogWarning(ex, "One or more cameras failed to start automatically. " +
                "The health watchdog will attempt recovery.");
        }
    }

    private async Task TryAsync(string stepName, Func<Task> action)
    {
        try
        {
            _logger.LogDebug("Teardown: {Step}…", stepName);
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Teardown step '{Step}' threw — continuing.", stepName);
        }
    }
}
