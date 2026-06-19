using System;

namespace VisionCore.Core.Models
{
    /// <summary>
    /// A single entry in the configuration audit log — records who changed
    /// what and when, for compliance/traceability in office deployments.
    ///
    /// Written by <see cref="VisionCore.Core.Services.SettingsService.WriteAuditLogAsync"/>
    /// and displayed in the WPF "Audit Log" view.
    /// </summary>
    public sealed class AuditLogEntry
    {
        /// <summary>UTC timestamp when the action occurred.</summary>
        public DateTime TimestampUtc { get; set; }

        /// <summary>
        /// Windows logon name of the user who performed the action
        /// (<see cref="Environment.UserName"/>). VisionCore has no separate
        /// login system, so the OS account is the audit identity.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Short machine-readable action code, e.g. "CameraAdded",
        /// "CameraDeleted", "CameraStarted", "CameraStopped",
        /// "SettingsSaved", "ConfigExported", "ConfigImported".
        /// </summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>The object the action applied to, e.g. a camera name.</summary>
        public string Target { get; set; } = string.Empty;

        /// <summary>Optional free-text details (e.g. what changed).</summary>
        public string Details { get; set; } = string.Empty;

        /// <summary>Local time for display in the UI.</summary>
        public DateTime TimestampLocal => TimestampUtc.ToLocalTime();
    }
}
