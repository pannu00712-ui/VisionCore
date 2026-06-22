namespace VisionCore.Service;

/// <summary>
/// Shared constants used by the service host, installer script, and IPC clients.
/// </summary>
internal static class ServiceConstants
{
    /// <summary>SCM service name (used with sc.exe and the installer).</summary>
    public const string ServiceName = "VisionCoreService";

    /// <summary>Human-readable display name shown in services.msc.</summary>
    public const string DisplayName = "VisionCore Streaming Engine";

    /// <summary>Description shown in services.msc.</summary>
    public const string Description =
        "Manages RTSP / ONVIF camera streams, motion detection, and the REST API " +
        "for the VisionCore surveillance platform.";

    /// <summary>Named pipe used for IPC between the WPF tray app and this service.</summary>
    public const string IpcPipeName = "VisionCoreServicePipe";
}
