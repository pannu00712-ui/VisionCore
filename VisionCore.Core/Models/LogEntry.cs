using System;

namespace VisionCore.Core.Models
{
    /// <summary>
    /// A single structured log entry held in the in-memory ring-buffer of
    /// <c>RestApiService</c> and exposed via <c>GET /api/v1/logs</c>.
    ///
    /// Created by a custom Serilog sink (<c>RestApiLogSink</c>) that forwards
    /// log events to <c>RestApiService.AddLog</c>.
    ///
    /// The buffer is capped at 1 000 entries (oldest evicted first) to avoid
    /// unbounded memory growth.  Entries are never persisted to disk through
    /// this path — use the Serilog file sink for durable logs.
    /// </summary>
    public sealed class LogEntry
    {
        // ── Core fields ───────────────────────────────────────────────────────

        /// <summary>UTC timestamp when the log event was emitted.</summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Serilog level as a string.
        /// Values: <c>"Verbose"</c>, <c>"Debug"</c>, <c>"Information"</c>,
        /// <c>"Warning"</c>, <c>"Error"</c>, <c>"Fatal"</c>.
        /// </summary>
        public string Level { get; set; } = "Information";

        /// <summary>
        /// Rendered log message (template variables already substituted).
        /// </summary>
        public string Message { get; set; } = string.Empty;

        // ── Optional context ──────────────────────────────────────────────────

        /// <summary>
        /// Name of the source context / logger category, e.g.
        /// <c>"VisionCore.Core.Services.CameraManager"</c>.
        /// Null if the event was emitted without a source context.
        /// </summary>
        public string? SourceContext { get; set; }

        /// <summary>
        /// Exception message and type if the log event included an exception.
        /// Format: <c>"ExceptionType: Message\nStackTrace"</c>.
        /// Null when no exception is attached.
        /// </summary>
        public string? ExceptionText { get; set; }

        /// <summary>
        /// Identifier of the camera this log entry relates to, if applicable.
        /// Populated from the Serilog <c>CameraId</c> enrichment property.
        /// Null for non-camera log events.
        /// </summary>
        public Guid? CameraId { get; set; }

        // ── Display helper ────────────────────────────────────────────────────

        /// <summary>
        /// One-letter severity abbreviation for compact UI display.
        /// V / D / I / W / E / F
        /// </summary>
        public string LevelShort => Level.Length > 0 ? Level[0].ToString().ToUpper() : "?";

        /// <inheritdoc />
        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss}] [{Level[..Math.Min(3, Level.Length)]}] {Message}";
    }
}
