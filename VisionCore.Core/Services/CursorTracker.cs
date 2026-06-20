using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace VisionCore.Core.Services
{
    // ══════════════════════════════════════════════════════════════════════════
    // CursorTracker
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Polls the Win32 cursor position at ~30 Hz and writes it to a tiny
    /// shared text file that the FFmpeg <c>geq</c> filter reads via the
    /// <c>sendcmd</c> / dynamic <c>drawtext</c> approach.
    ///
    /// The file format is a single line:  <c>x,y</c>  (e.g. <c>1024,768</c>).
    ///
    /// FFmpeg side (built by <c>RtspStreamManager.BuildFilters</c>) uses:
    /// <code>
    ///   [in]crop=w:h:0:0,geq=... overlay ...
    /// </code>
    /// with coordinates read from the shared file path
    /// (<see cref="GetPositionFilePath"/>).
    ///
    /// A single static instance is used — all cameras share one poll loop.
    /// </summary>
    public static class CursorTracker
    {
        // ── Win32 ─────────────────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        // ── Shared position file ──────────────────────────────────────────────

        private static readonly string _posFile =
            Path.Combine(Path.GetTempPath(), "visioncore_cursor.txt");

        private static readonly string _cmdFile =
            Path.Combine(Path.GetTempPath(), "visioncore_cursor_cmd.txt");

        private static Thread?  _thread;
        private static volatile bool _running;
        private static int _refCount;
        private static readonly object _lock = new();

        /// <summary>Path to the position file — pass to FFmpeg filter args.</summary>
        public static string GetPositionFilePath() => _posFile;

        /// <summary>Path to the FFmpeg sendcmd script file for drawbox repositioning.</summary>
        public static string GetSendcmdFilePath() => _cmdFile;

        // ── Lifecycle ─────────────────────────────────────────────────────────

        /// <summary>
        /// Increment the reference count; starts the polling thread on first call.
        /// Call once per camera that enables a Cursor overlay.
        /// </summary>
        public static void AddRef()
        {
            lock (_lock)
            {
                _refCount++;
                if (_refCount == 1)
                    Start();
            }
        }

        /// <summary>
        /// Decrement the reference count; stops the polling thread when it reaches 0.
        /// </summary>
        public static void Release()
        {
            lock (_lock)
            {
                _refCount = Math.Max(0, _refCount - 1);
                if (_refCount == 0)
                    Stop();
            }
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private static void Start()
        {
            _running = true;
            _thread = new Thread(PollLoop)
            {
                IsBackground = true,
                Name = "CursorTracker",
            };
            _thread.Start();
        }

        private static void Stop()
        {
            _running = false;
            try { File.Delete(_posFile); } catch { /* best-effort */ }
            try { File.Delete(_cmdFile);  } catch { /* best-effort */ }
        }

        private static void PollLoop()
        {
            while (_running)
            {
                try
                {
                    if (GetCursorPos(out var pt))
                    {
                        File.WriteAllText(_posFile, $"{pt.X},{pt.Y}");

                        // Write an FFmpeg sendcmd script that repositions the drawbox
                        // each time the file is reloaded.  Format:
                        //   0 drawbox x {X}, drawbox y {Y};
                        File.WriteAllText(_cmdFile,
                            $"0 drawbox x {pt.X}, drawbox y {pt.Y};\n");
                    }
                }
                catch { /* ignore I/O errors */ }

                Thread.Sleep(33); // ~30 Hz
            }
        }

        /// <summary>
        /// Read current cursor position.  Returns (0,0) on failure.
        /// </summary>
        public static (int X, int Y) GetPosition()
        {
            try
            {
                var text = File.ReadAllText(_posFile);
                var parts = text.Split(',');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var x) &&
                    int.TryParse(parts[1], out var y))
                    return (x, y);
            }
            catch { /* file may not exist yet */ }
            return (0, 0);
        }
    }
}
