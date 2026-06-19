using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    // ══════════════════════════════════════════════════════════════════════════
    // InputActivityMonitor
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Installs a single process-wide low-level keyboard + mouse hook and
    /// translates activity into per-camera motion events for cameras that
    /// have <see cref="CameraConfig.MotionOnInputActivity"/> enabled.
    ///
    /// Architecture:
    ///   Win32 LowLevelKeyboardProc / LowLevelMouseProc
    ///       └─▶  InputActivityMonitor  (sets _lastActivity, refreshes hold timer)
    ///               └─▶  MotionDetector.TriggerInputMotion(cameraId, isMotion)
    ///
    /// Only ONE hook pair is installed regardless of how many cameras use this
    /// feature, keeping overhead minimal.
    /// </summary>
    public sealed class InputActivityMonitor : IDisposable
    {
        // ── Win32 ─────────────────────────────────────────────────────────────

        private const int WH_KEYBOARD_LL = 13;
        private const int WH_MOUSE_LL    = 14;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook, HookProc lpfn, IntPtr hmod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        // ── Per-camera state ──────────────────────────────────────────────────

        private sealed class CameraEntry
        {
            public CameraConfig Config     { get; }
            public bool         IsMotion   { get; set; }
            public DateTime     LastInput  { get; set; } = DateTime.MinValue;

            public CameraEntry(CameraConfig c) { Config = c; }
        }

        // ── Fields ────────────────────────────────────────────────────────────

        private readonly ILogger<InputActivityMonitor> _logger;
        private readonly MotionDetector               _motion;

        private readonly ConcurrentDictionary<Guid, CameraEntry> _cameras = new();

        // Hold-time sweep timer (fires every 500 ms)
        private readonly System.Threading.Timer _sweepTimer;

        // Hooks — kept alive to prevent GC
        private IntPtr   _keyboardHook = IntPtr.Zero;
        private IntPtr   _mouseHook    = IntPtr.Zero;
        private HookProc? _keyboardProc;
        private HookProc? _mouseProc;

        private long _lastActivityTicks = DateTime.MinValue.Ticks;
        private bool _disposed;

        // ── Constructor ───────────────────────────────────────────────────────

        public InputActivityMonitor(
            ILogger<InputActivityMonitor> logger,
            MotionDetector motion)
        {
            _logger = logger;
            _motion = motion;
            _sweepTimer = new System.Threading.Timer(SweepCallback, null, Timeout.Infinite, Timeout.Infinite);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Begin watching a camera for input-driven motion events.
        /// Safe to call multiple times for the same camera (idempotent).
        /// Installs the Win32 hooks on first registration.
        /// </summary>
        public void Register(CameraConfig cam)
        {
            _cameras[cam.Id] = new CameraEntry(cam);
            _logger.LogDebug("[InputActivity] Registered camera '{Name}' (hold={Hold}s).",
                cam.Name, cam.InputMotionHoldTime.TotalSeconds);
            EnsureHooksInstalled();
        }

        /// <summary>Unregister a camera. Removes hooks when no cameras remain.</summary>
        public void Unregister(Guid cameraId)
        {
            if (_cameras.TryRemove(cameraId, out var entry))
            {
                // Clear motion state if it was active
                if (entry.IsMotion)
                    _motion.TriggerInputMotion(cameraId, false);

                _logger.LogDebug("[InputActivity] Unregistered camera {Id}.", cameraId);
            }

            if (_cameras.IsEmpty)
                RemoveHooks();
        }

        /// <summary>
        /// Externally inject an activity pulse — equivalent to a key/mouse event.
        /// Used by the Manual Trigger REST endpoint.
        /// </summary>
        public void InjectActivity()
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            OnActivity();
        }

        // ── Hook management ───────────────────────────────────────────────────

        private void EnsureHooksInstalled()
        {
            if (_keyboardHook != IntPtr.Zero) return; // already installed

            var hmod = GetModuleHandle(null);

            _keyboardProc = KeyboardHookCallback;
            _mouseProc    = MouseHookCallback;

            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hmod, 0);
            _mouseHook    = SetWindowsHookEx(WH_MOUSE_LL,    _mouseProc,    hmod, 0);

            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                _logger.LogWarning("[InputActivity] Could not install Win32 hooks " +
                    "(error {Err}). Input-motion feature disabled.",
                    Marshal.GetLastWin32Error());
                return;
            }

            // Start the hold-time sweep
            _sweepTimer.Change(500, 500);
            _logger.LogInformation("[InputActivity] Low-level keyboard+mouse hooks installed.");
        }

        private void RemoveHooks()
        {
            _sweepTimer.Change(Timeout.Infinite, Timeout.Infinite);

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            _logger.LogInformation("[InputActivity] Hooks removed.");
        }

        // ── Hook callbacks ────────────────────────────────────────────────────

        private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                OnActivity();
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
                OnActivity();
            }
            return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // ── Activity handling ─────────────────────────────────────────────────

        private void OnActivity()
        {
            foreach (var entry in _cameras.Values)
            {
                if (!entry.IsMotion)
                {
                    entry.IsMotion  = true;
                    entry.LastInput = DateTime.UtcNow;
                    _motion.TriggerInputMotion(entry.Config.Id, true);
                }
                else
                {
                    // Refresh hold timer
                    entry.LastInput = DateTime.UtcNow;
                }
            }
        }

        /// <summary>Fired every 500 ms — clears motion state once hold time expires.</summary>
        private void SweepCallback(object? _)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _cameras.Values)
            {
                if (!entry.IsMotion) continue;

                var elapsed = now - entry.LastInput;
                if (elapsed >= entry.Config.InputMotionHoldTime)
                {
                    entry.IsMotion = false;
                    _motion.TriggerInputMotion(entry.Config.Id, false);
                }
            }
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sweepTimer.Dispose();
            RemoveHooks();
        }
    }
}
