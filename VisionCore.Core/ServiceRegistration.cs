using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Interfaces;
using VisionCore.Core.Services;

namespace VisionCore.Core
{
    /// <summary>
    /// Extension methods for registering all VisionCore core services
    /// in the WPF app's IHostBuilder / IServiceCollection.
    ///
    /// Usage in App.xaml.cs:
    ///   _host = Host.CreateDefaultBuilder()
    ///       .UseSerilog()
    ///       .ConfigureServices((ctx, services) =>
    ///       {
    ///           services.AddVisionCoreServices();
    ///           // Add WPF ViewModels / Views here
    ///       })
    ///       .Build();
    /// </summary>
    public static class ServiceRegistration
    {
        public static IServiceCollection AddVisionCoreServices(
            this IServiceCollection services)
        {
            // ── Settings (SQLite) ──────────────────────────────────────────
            services.AddSingleton<SettingsService>();

            // ── RTSP layer ─────────────────────────────────────────────────
            services.AddSingleton<RtspServer>();
            services.AddSingleton<RtspStreamManager>();
            services.AddSingleton<RtspHealthMonitor>();
            services.AddSingleton<IRtspServer>(sp => sp.GetRequiredService<RtspServer>());

            // ── ONVIF ──────────────────────────────────────────────────────
            services.AddSingleton<OnvifServer>();
            services.AddSingleton<IOnvifServer>(sp => sp.GetRequiredService<OnvifServer>());

            // ── Motion detection ───────────────────────────────────────────
            services.AddSingleton<MotionDetector>();
            services.AddSingleton<InputActivityMonitor>();
            // CursorTracker is a static utility class — accessed directly, not via DI

            // ── Camera manager ─────────────────────────────────────────────
            services.AddSingleton<CameraManager>();

            // ── REST API ───────────────────────────────────────────────────
            services.AddSingleton<RestApiService>();

            // ── First-run / auto-update utilities ─────────────────────────
            services.AddSingleton<MediaMtxDownloader>();

            return services;
        }
    }
}
