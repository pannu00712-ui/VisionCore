using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Orchestrates the lifecycle of all configured cameras.
    /// Delegates stream publishing to <see cref="RtspStreamManager"/>,
    /// registers ONVIF devices with <see cref="OnvifServer"/>,
    /// and wires per-camera motion detection to ONVIF motion events.
    /// </summary>
    public sealed class CameraManager
    {
        private readonly ILogger<CameraManager>  _logger;
        private readonly SettingsService         _settings;
        private readonly RtspStreamManager       _streamManager;
        private readonly OnvifServer             _onvif;
        private readonly MotionDetector          _motion;

        private readonly InputActivityMonitor _inputMonitor;

        public CameraManager(
            ILogger<CameraManager> logger,
            SettingsService        settings,
            RtspStreamManager      streamManager,
            OnvifServer            onvif,
            MotionDetector         motion,
            InputActivityMonitor   inputMonitor)
        {
            _logger        = logger;
            _settings      = settings;
            _streamManager = streamManager;
            _onvif         = onvif;
            _motion        = motion;
            _inputMonitor  = inputMonitor;

            // Forward motion events → ONVIF motion notifications (subscribed once)
            _motion.MotionDetected += OnMotionDetected;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Start all cameras that have Enabled = true.</summary>
        public async Task StartAllAsync(CancellationToken ct = default)
        {
            foreach (var cam in _settings.Cameras)
            {
                if (!cam.Enabled) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    await StartCameraAsync(cam, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start camera '{Name}'.", cam.Name);
                }
            }
        }

        /// <summary>Stop all running cameras.</summary>
        public async Task StopAllAsync()
        {
            await _streamManager.StopAllAsync();
            await _motion.StopAllAsync();
            // ONVIF devices are unregistered individually; clear all here
            foreach (var cam in _settings.Cameras)
                _onvif.UnregisterDevice(cam.Id);
        }

        /// <summary>Start a single camera by ID.</summary>
        public async Task StartCameraAsync(Guid id, CancellationToken ct = default)
        {
            var cam = FindCamera(id);
            if (cam == null)
            {
                _logger.LogWarning("StartCamera: camera {Id} not found.", id);
                return;
            }
            await StartCameraAsync(cam, ct);
        }

        /// <summary>Stop a single camera by ID.</summary>
        public async Task StopCameraAsync(Guid id)
        {
            await _streamManager.StopPublisherAsync(id);
            await _motion.StopCameraAsync(id);
            _onvif.UnregisterDevice(id);
        }

        /// <summary>Start a camera by its config object (overload used by REST API).</summary>
        public async Task StartCameraAsync(CameraConfig cam, CancellationToken ct = default)
        {
            await _streamManager.StartPublisherAsync(cam, ct);

            if (cam.OnvifEnabled)
                _onvif.RegisterDevice(cam);

            if (cam.MotionDetection)
                _motion.StartCamera(cam);

            // Input-activity motion trigger
            if (cam.MotionOnInputActivity)
                _inputMonitor.Register(cam);
        }

        /// <summary>Returns true if the camera stream is currently publishing.</summary>
        public bool IsRunning(Guid id) => _streamManager.IsPublisherRunning(id);

        /// <summary>Total number of connected RTSP clients across all cameras.</summary>
        public int TotalConnections => _streamManager.TotalConnections;

        /// <summary>Get stats snapshot for a single camera, or null if not found.</summary>
        public CameraStats? GetStats(Guid id) => _streamManager.GetStats(id);

        /// <summary>Get stats for all cameras keyed by camera ID.</summary>
        public Dictionary<Guid, CameraStats> GetAllStats() => _streamManager.GetAllStats();

        /// <summary>
        /// Captures a single JPEG snapshot from a running camera's stream.
        /// Returns null if the camera isn't running or capture failed.
        /// </summary>
        public Task<byte[]?> CaptureSnapshotAsync(Guid cameraId, CancellationToken ct = default)
            => _streamManager.CaptureSnapshotAsync(cameraId, ct);

        // ── Private helpers ───────────────────────────────────────────────────

        private void OnMotionDetected(object? sender, MotionEventArgs e)
            => _onvif.NotifyMotion(e.CameraId, e.IsMotion);

        private CameraConfig? FindCamera(Guid id)
        {
            foreach (var cam in _settings.Cameras)
                if (cam.Id == id) return cam;
            return null;
        }
    }
}
