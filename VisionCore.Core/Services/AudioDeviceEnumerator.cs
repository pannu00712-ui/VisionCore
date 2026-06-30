using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Enumerates audio capture (microphone) and render (speaker / virtual) devices
    /// available on the system for use with FFmpeg's dshow input filter.
    ///
    /// Usage:
    ///   var enumerator = new AudioDeviceEnumerator(logger);
    ///   var mics  = enumerator.GetMicrophoneDevices();
    ///   var sys   = enumerator.GetVirtualAudioDevices();   // e.g. virtual-audio-capturer
    ///
    /// Returned <see cref="AudioDevice.FfmpegName"/> can be passed directly to
    /// the FFmpeg dshow argument:  -f dshow -i audio="<FfmpegName>"
    ///
    /// Requires:
    ///   Windows only (uses DirectShow / Win32 registry enumeration).
    ///   On non-Windows the lists will be empty and a warning is logged.
    /// </summary>
    public sealed class AudioDeviceEnumerator
    {
        private readonly ILogger<AudioDeviceEnumerator> _logger;

        public AudioDeviceEnumerator(ILogger<AudioDeviceEnumerator> logger)
        {
            _logger = logger;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all DirectShow audio capture devices (microphones, line-in, etc.).
        /// </summary>
        public IReadOnlyList<AudioDevice> GetMicrophoneDevices()
        {
            return EnumerateDirectShowDevices(DirectShowCategory.AudioCapture);
        }

        /// <summary>
        /// Returns virtual audio loopback devices (e.g. VB-Cable, Virtual Audio Capturer).
        /// These are used to record system audio.
        /// </summary>
        public IReadOnlyList<AudioDevice> GetVirtualAudioDevices()
        {
            return EnumerateDirectShowDevices(DirectShowCategory.AudioRender);
        }

        /// <summary>
        /// Returns all audio devices combined (capture + virtual render).
        /// </summary>
        public IReadOnlyList<AudioDevice> GetAllAudioDevices()
        {
            var all = new List<AudioDevice>();
            all.AddRange(GetMicrophoneDevices());
            all.AddRange(GetVirtualAudioDevices());
            return all;
        }

        /// <summary>
        /// Returns the default microphone device name as reported by Windows.
        /// Returns null if no microphone is found.
        /// </summary>
        public AudioDevice? GetDefaultMicrophone()
        {
            var mics = GetMicrophoneDevices();
            return mics.Count > 0 ? mics[0] : null;
        }

        // ── Enumeration ───────────────────────────────────────────────────────

        private IReadOnlyList<AudioDevice> EnumerateDirectShowDevices(DirectShowCategory category)
        {
            var results = new List<AudioDevice>();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _logger.LogWarning("AudioDeviceEnumerator: DirectShow enumeration is Windows-only.");
                return results;
            }

            try
            {
                results.AddRange(EnumerateViaRegistry(category));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate DirectShow audio devices.");
            }

            return results;
        }

        /// <summary>
        /// Reads device friendly names from the Windows registry.
        /// DirectShow audio devices are listed under:
        ///   HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\{category-GUID}
        ///
        /// As a simpler fallback, we also read from the dshow-friendly path via
        ///   HKCU\Software\Microsoft\ActiveMovie\devenum\{category}
        /// </summary>
        private static IEnumerable<AudioDevice> EnumerateViaRegistry(DirectShowCategory category)
        {
            var results = new List<AudioDevice>();
            var categoryGuid = category == DirectShowCategory.AudioCapture
                ? "{33D9A762-90C8-11d0-BD43-00A0C911CE86}"   // CLSID_AudioInputDeviceCategory
                : "{E0F158E1-CB04-11d0-BD4E-00A0C911CE86}";  // CLSID_AudioRendererCategory

            var regPath = $@"Software\Microsoft\ActiveMovie\devenum\{categoryGuid}";

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(regPath);
            if (key == null) return results;

            foreach (var valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;

                // The value name IS the friendly device name for dshow
                var friendlyName = valueName;

                results.Add(new AudioDevice
                {
                    FriendlyName = friendlyName,
                    FfmpegName   = friendlyName,
                    DeviceType   = category == DirectShowCategory.AudioCapture
                        ? AudioDeviceType.Microphone
                        : AudioDeviceType.VirtualLoopback,
                });
            }

            return results;
        }

        // ── Inner types ───────────────────────────────────────────────────────

        private enum DirectShowCategory
        {
            AudioCapture,
            AudioRender,
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Supporting types
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Represents a single audio device available for recording.</summary>
    public sealed class AudioDevice
    {
        /// <summary>Human-readable device name shown in the UI.</summary>
        public string FriendlyName { get; init; } = string.Empty;

        /// <summary>
        /// Name to pass to FFmpeg's dshow filter.
        /// Usage: <c>-f dshow -i audio="FfmpegName"</c>
        /// </summary>
        public string FfmpegName { get; init; } = string.Empty;

        /// <summary>Whether this is a physical microphone or a virtual loopback.</summary>
        public AudioDeviceType DeviceType { get; init; } = AudioDeviceType.Microphone;

        public override string ToString() => FriendlyName;
    }

    /// <summary>Category of audio device.</summary>
    public enum AudioDeviceType
    {
        /// <summary>Physical microphone or line-in capture device.</summary>
        Microphone,

        /// <summary>Virtual loopback device for capturing system audio output.</summary>
        VirtualLoopback,
    }
}
