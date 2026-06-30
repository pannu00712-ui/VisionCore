using System.Collections.Generic;
namespace VisionCore.Core.Models
{
    /// <summary>
    /// Controls how VisionCore exposes virtual cameras to ONVIF clients (NVRs/VMSes).
    ///
    /// MultipleCamera (default) — mirrors DeskCamera "Multiple Cameras" mode:
    ///   each CameraConfig gets its own ONVIF IP camera on a dedicated HTTP port
    ///   (OnvifPort in CameraConfig, e.g. 8080, 8081, 8082…).
    ///   NVR discovers N separate IP cameras, each with one RTSP stream.
    ///
    /// MultipleChannel — mirrors DeskCamera "Multiple Channels" mode:
    ///   all cameras are exposed as a single ONVIF IP camera on one shared port
    ///   (AppSettings.OnvifSharedPort, default 8090).
    ///   NVR discovers one IP camera with N media profiles / channels.
    ///   Some NVRs (e.g. Synology Surveillance Station) prefer this.
    /// </summary>
    public enum OnvifMode
    {
        /// <summary>One ONVIF IP camera per CameraConfig (default).</summary>
        MultipleCamera,

        /// <summary>All cameras as channels of a single ONVIF IP camera.</summary>
        MultipleChannel,
    }

    /// <summary>
    /// Application-wide settings persisted as a single JSON row in the
    /// SQLite <c>app_settings</c> table.
    ///
    /// Loaded by <c>SettingsService</c> at startup and cached in memory.
    /// Written back on every explicit Save from <c>SettingsViewModel</c>.
    ///
    /// Adding new properties is safe — the JSON deserialiser ignores unknown keys
    /// and missing keys fall back to C# property defaults.
    /// </summary>
    public sealed class AppSettings
    {
        // ── General ───────────────────────────────────────────────────────────

        /// <summary>
        /// Active UI theme.
        /// Values: <c>"Dark"</c> (default), <c>"Light"</c>, <c>"System"</c>.
        /// App.xaml.cs reads this on startup and whenever the user changes it.
        /// </summary>
        public string Theme { get; set; } = "Dark";

        /// <summary>
        /// Whether to register a startup entry in
        /// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
        /// </summary>
        public bool StartWithWindows { get; set; } = false;

        /// <summary>
        /// Whether the WPF tray app should connect to the Windows Service
        /// rather than hosting the engine in-process.
        /// When true, the app uses the named pipe IPC channel instead of
        /// direct service references.
        /// </summary>
        public bool RunAsService { get; set; } = false;

        /// <summary>
        /// When true, closing the main window sends the app to the system tray
        /// instead of terminating the process.
        /// </summary>
        public bool MinimizeToTray { get; set; } = true;

        /// <summary>
        /// Hidden / Stealth Mode — mirrors DeskCamera's Hidden Mode.
        /// When true, the application starts with no visible window and no tray icon.
        /// The engine runs silently in the background.
        /// Press <c>Ctrl+Alt+D</c> to reveal the UI at any time.
        /// </summary>
        public bool StealthMode { get; set; } = false;

        // ── ONVIF mode ────────────────────────────────────────────────────────

        /// <summary>
        /// Whether to expose one ONVIF camera per channel (MultipleCamera)
        /// or all cameras as profiles of a single ONVIF device (MultipleChannel).
        /// See <see cref="OnvifMode"/> for full description.
        /// </summary>
        public OnvifMode OnvifMode { get; set; } = OnvifMode.MultipleCamera;

        /// <summary>
        /// HTTP port used in MultipleChannel mode for the single shared ONVIF listener.
        /// Default 8090 (matches DeskCamera default).
        /// </summary>
        public int OnvifSharedPort { get; set; } = 8090;

        // ── REST API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Whether the embedded ASP.NET Core REST API host is started.
        /// Disabling it also disables Swagger and the /health endpoint.
        /// </summary>
        public bool RestApiEnabled { get; set; } = true;

        /// <summary>Port the REST API listens on (default 7880).</summary>
        public int RestApiPort { get; set; } = 7880;

        /// <summary>
        /// Bearer token required for all authenticated REST endpoints.
        /// Empty string means no authentication enforced.
        /// Generated via the Settings page "Regenerate" button.
        /// </summary>
        public string? RestApiToken { get; set; }

        // ── MediaMTX ──────────────────────────────────────────────────────────

        /// <summary>
        /// Absolute path to the MediaMTX binary used for RTSP serving.
        /// Null means use the default path: <c>%LOCALAPPDATA%\VisionCore\bin\mediamtx.exe</c>.
        /// Overridable by the user in Settings → MediaMTX.
        /// </summary>
        public string? MediaMtxPath { get; set; }

        /// <summary>Port MediaMTX listens on for RTSP connections (default 8554).</summary>
        public int RtspPort { get; set; } = 8554;

        // ── UPnP ──────────────────────────────────────────────────────────────

        /// <summary>
        /// When true, VisionCore attempts to open port mappings on the user's
        /// router via UPnP IGD on service start (RTSP, REST API, and the
        /// configured ONVIF port range), and removes them again on service stop.
        /// Default false — exposing ports to the internet is opt-in and the
        /// resulting status (success / failed / disabled) is always shown in
        /// Settings so the user knows what is exposed.
        /// </summary>
        public bool EnableUpnp { get; set; } = false;

        // ── Logging ───────────────────────────────────────────────────────────

        /// <summary>
        /// Minimum Serilog log level.
        /// Values: <c>"Verbose"</c>, <c>"Debug"</c>, <c>"Information"</c> (default),
        /// <c>"Warning"</c>, <c>"Error"</c>, <c>"Fatal"</c>.
        /// Applied dynamically via a <c>LoggingLevelSwitch</c> — no restart needed.
        /// </summary>
        public string? LogLevel { get; set; } = "Information";

        // ── Updates ───────────────────────────────────────────────────────────

        /// <summary>
        /// Whether to check for application updates automatically on startup.
        /// </summary>
        public bool AutoCheckUpdates { get; set; } = true;

        /// <summary>
        /// Base URL of the update manifest endpoint.
        /// Default: <c>https://updates.visioncore.app/manifest.json</c>.
        /// Overridable for enterprise self-hosted update servers.
        /// </summary>
        public string UpdateManifestUrl { get; set; } =
            "https://updates.visioncore.app/manifest.json";

        // ── REST API Users ────────────────────────────────────────────────────

        /// <summary>
        /// List of usernames permitted to authenticate via the REST API.
        /// Defaults to a single "admin" user.
        /// </summary>
        public List<string> AdminUsers { get; set; } = new List<string> { "admin" };

    }
}
