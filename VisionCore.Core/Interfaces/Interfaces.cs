using System;
using System.Threading;
using System.Threading.Tasks;
using VisionCore.Core.Models;

namespace VisionCore.Core.Interfaces
{
    public interface IRtspServer
    {
        Task StartAsync(int port, CancellationToken ct = default);
        Task StopAsync();
        int  GetClientCount(Guid cameraId);
    }

    public interface IOnvifServer
    {
        Task StartAsync(CancellationToken ct = default);
        Task StopAsync();
        void RegisterDevice(CameraConfig config);
        void UnregisterDevice(Guid id);
        void NotifyMotion(Guid cameraId, bool isMotion);
    }

    /// <summary>
    /// Provides raw video frames to the RTSP pipeline.
    /// Implemented by FFmpegEngine / CameraManager.
    /// </summary>
    public interface IFrameSource
    {
        /// <summary>Latest encoded frame (H.264/H.265 NAL units), or null if not yet available.</summary>
        byte[]? GetLatestFrame();

        /// <summary>Fired each time a new encoded frame is ready.</summary>
        event EventHandler<byte[]>? FrameReady;
    }
}
