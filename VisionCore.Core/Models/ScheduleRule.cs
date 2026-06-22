using System;
using System.Collections.Generic;

namespace VisionCore.Core.Models
{
    /// <summary>
    /// A single time-window rule that controls when a camera is allowed to stream.
    ///
    /// A camera can have zero or more <see cref="ScheduleRule"/> entries in
    /// <c>CameraConfig.ScheduleRules</c> (add
    /// <c>public List&lt;ScheduleRule&gt; ScheduleRules { get; set; } = new();</c>
    /// to CameraConfig).
    ///
    /// Evaluation logic (implemented in <c>SchedulerService</c>):
    ///   • If the list is empty → camera is always allowed to run.
    ///   • If the list has entries → camera runs ONLY when at least one enabled
    ///     rule's window covers the current local time.
    ///   • <see cref="DaysOfWeek"/> determines which days the window applies.
    ///   • <see cref="StartTime"/> / <see cref="EndTime"/> are wall-clock times on those days.
    ///   • An overnight window (e.g. 22:00 – 06:00) is supported:
    ///     when EndTime &lt; StartTime the window wraps past midnight.
    /// </summary>
    public sealed class ScheduleRule
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>Stable identifier; auto-assigned on creation.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Human-readable label shown in the schedule editor.</summary>
        public string Label { get; set; } = "Work hours";

        /// <summary>Whether this rule is currently active.</summary>
        public bool Enabled { get; set; } = true;

        // ── Days ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Bit-flag set of weekdays this rule applies to.
        /// Uses <see cref="System.DayOfWeek"/> values as flag positions:
        ///   Sunday=0, Monday=1, … Saturday=6.
        /// Default: Monday–Friday.
        /// </summary>
        public ScheduleDays DaysOfWeek { get; set; } =
            ScheduleDays.Monday | ScheduleDays.Tuesday | ScheduleDays.Wednesday |
            ScheduleDays.Thursday | ScheduleDays.Friday;

        // ── Time window ───────────────────────────────────────────────────────

        /// <summary>
        /// Start of the allowed window (local time).
        /// Example: <c>new TimeSpan(9, 0, 0)</c> = 09:00.
        /// </summary>
        public TimeSpan StartTime { get; set; } = new TimeSpan(9, 0, 0);

        /// <summary>
        /// End of the allowed window (local time, exclusive).
        /// When EndTime &lt; StartTime the window wraps past midnight.
        /// Example: <c>new TimeSpan(18, 0, 0)</c> = 18:00.
        /// </summary>
        public TimeSpan EndTime { get; set; } = new TimeSpan(18, 0, 0);

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Returns true if <paramref name="now"/> falls inside this rule's window.</summary>
        public bool IsActive(DateTime now)
        {
            if (!Enabled) return false;

            var dayFlag = DayToFlag(now.DayOfWeek);
            if ((DaysOfWeek & dayFlag) == 0) return false;

            var tod = now.TimeOfDay;

            if (EndTime >= StartTime)
            {
                // Normal window (e.g. 09:00–18:00)
                return tod >= StartTime && tod < EndTime;
            }
            else
            {
                // Overnight window (e.g. 22:00–06:00):
                // active if after start OR before end (crosses midnight)
                return tod >= StartTime || tod < EndTime;
            }
        }

        private static ScheduleDays DayToFlag(DayOfWeek d) => d switch
        {
            DayOfWeek.Sunday    => ScheduleDays.Sunday,
            DayOfWeek.Monday    => ScheduleDays.Monday,
            DayOfWeek.Tuesday   => ScheduleDays.Tuesday,
            DayOfWeek.Wednesday => ScheduleDays.Wednesday,
            DayOfWeek.Thursday  => ScheduleDays.Thursday,
            DayOfWeek.Friday    => ScheduleDays.Friday,
            DayOfWeek.Saturday  => ScheduleDays.Saturday,
            _                   => ScheduleDays.None,
        };

        /// <summary>Display string shown in the schedule list (e.g. "Work hours: Mon–Fri 09:00–18:00").</summary>
        public override string ToString()
        {
            var days  = FormatDays(DaysOfWeek);
            var start = FormatTime(StartTime);
            var end   = FormatTime(EndTime);
            return $"{Label}: {days} {start}–{end}";
        }

        private static string FormatTime(TimeSpan t)
            => $"{t.Hours:D2}:{t.Minutes:D2}";

        private static string FormatDays(ScheduleDays d)
        {
            if (d == (ScheduleDays.Monday | ScheduleDays.Tuesday | ScheduleDays.Wednesday |
                      ScheduleDays.Thursday | ScheduleDays.Friday))
                return "Mon–Fri";
            if (d == (ScheduleDays.Saturday | ScheduleDays.Sunday))
                return "Weekends";
            if (d == (ScheduleDays.Monday | ScheduleDays.Tuesday | ScheduleDays.Wednesday |
                      ScheduleDays.Thursday | ScheduleDays.Friday |
                      ScheduleDays.Saturday | ScheduleDays.Sunday))
                return "Every day";

            var parts = new List<string>();
            if (d.HasFlag(ScheduleDays.Monday))    parts.Add("Mon");
            if (d.HasFlag(ScheduleDays.Tuesday))   parts.Add("Tue");
            if (d.HasFlag(ScheduleDays.Wednesday)) parts.Add("Wed");
            if (d.HasFlag(ScheduleDays.Thursday))  parts.Add("Thu");
            if (d.HasFlag(ScheduleDays.Friday))    parts.Add("Fri");
            if (d.HasFlag(ScheduleDays.Saturday))  parts.Add("Sat");
            if (d.HasFlag(ScheduleDays.Sunday))    parts.Add("Sun");
            return string.Join(", ", parts);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ScheduleDays — bit-flag enum matching System.DayOfWeek positions
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bit-flag enum for selecting days of the week in a schedule rule.
    /// Values are powers of two aligned to <see cref="System.DayOfWeek"/>:
    ///   Sunday = 1, Monday = 2, …, Saturday = 64.
    /// </summary>
    [Flags]
    public enum ScheduleDays
    {
        None      = 0,
        Sunday    = 1 << 0,   // 1
        Monday    = 1 << 1,   // 2
        Tuesday   = 1 << 2,   // 4
        Wednesday = 1 << 3,   // 8
        Thursday  = 1 << 4,   // 16
        Friday    = 1 << 5,   // 32
        Saturday  = 1 << 6,   // 64
    }
}
