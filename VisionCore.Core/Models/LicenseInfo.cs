using System;

namespace VisionCore.Core.Models
{
    /// <summary>
    /// Represents a validated VisionCore license.
    ///
    /// The license key is a signed JWT:
    ///   Header   : { "alg": "HS256", "typ": "lic" }
    ///   Payload  : <see cref="LicenseInfo"/> fields as JSON claims
    ///   Signature: HMAC-SHA256 with the server-side secret (never shipped in the app)
    ///
    /// Validation flow (in <c>LicenseService</c>):
    ///   1. Decode and verify signature against the embedded public key.
    ///   2. Check <see cref="ExpiresUtc"/> (for time-limited licenses).
    ///   3. Check <see cref="MachineId"/> matches <see cref="LicenseService.GetMachineId"/>.
    ///   4. Cache result in <c>%LOCALAPPDATA%\VisionCore\license.dat</c> (encrypted).
    ///
    /// Tiers:
    ///   Free     — up to <c>MaxCameras = 1</c>, no recording, no schedule.
    ///   Pro      — unlimited cameras, all features.
    ///   Enterprise — Pro + multi-machine site license.
    /// </summary>
    public sealed class LicenseInfo
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>Unique license key string entered by the user.</summary>
        public string LicenseKey { get; set; } = string.Empty;

        /// <summary>Name of the licensed customer (shown in the About dialog).</summary>
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>Customer email address (informational only).</summary>
        public string CustomerEmail { get; set; } = string.Empty;

        // ── Tier & limits ─────────────────────────────────────────────────────

        /// <summary>Product tier controlling which features are unlocked.</summary>
        public LicenseTier Tier { get; set; } = LicenseTier.Free;

        /// <summary>
        /// Maximum number of cameras that may run simultaneously.
        /// -1 = unlimited (Pro / Enterprise).
        /// </summary>
        public int MaxCameras { get; set; } = 1;

        // ── Dates ─────────────────────────────────────────────────────────────

        /// <summary>UTC date/time when the license was issued.</summary>
        public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC date/time after which the license is no longer valid.
        /// <see cref="DateTime.MaxValue"/> means perpetual (no expiry).
        /// </summary>
        public DateTime ExpiresUtc { get; set; } = DateTime.MaxValue;

        // ── Machine binding ───────────────────────────────────────────────────

        /// <summary>
        /// Hardware fingerprint this license is bound to.
        /// Null = site license (any machine).
        /// </summary>
        public string? MachineId { get; set; }

        // ── Feature flags ─────────────────────────────────────────────────────

        /// <summary>Whether local MP4 recording is unlocked.</summary>
        public bool RecordingEnabled { get; set; } = false;

        /// <summary>Whether schedule-based start/stop is unlocked.</summary>
        public bool SchedulingEnabled { get; set; } = false;

        /// <summary>Whether the REST API is unlocked.</summary>
        public bool RestApiEnabled { get; set; } = false;

        // ── Computed ──────────────────────────────────────────────────────────

        /// <summary>True if the license has not yet expired.</summary>
        public bool IsExpired => DateTime.UtcNow > ExpiresUtc;

        /// <summary>True if this is any paid tier.</summary>
        public bool IsPaid => Tier != LicenseTier.Free;

        /// <summary>Days remaining until expiry; 0 if already expired; null if perpetual.</summary>
        public int? DaysRemaining
        {
            get
            {
                if (ExpiresUtc == DateTime.MaxValue) return null;
                var days = (int)(ExpiresUtc - DateTime.UtcNow).TotalDays;
                return Math.Max(0, days);
            }
        }

        /// <summary>Friendly label for the tier shown in the UI.</summary>
        public string TierLabel => Tier switch
        {
            LicenseTier.Pro        => "Pro",
            LicenseTier.Enterprise => "Enterprise",
            _                      => "Free",
        };

        /// <summary>Returns a built-in Free license instance.</summary>
        public static LicenseInfo CreateFree() => new()
        {
            Tier              = LicenseTier.Free,
            MaxCameras        = 1,
            ExpiresUtc        = DateTime.MaxValue,
            RecordingEnabled  = false,
            SchedulingEnabled = false,
            RestApiEnabled    = false,
            CustomerName      = "Unregistered",
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LicenseTier
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Product tier that determines which features are available.</summary>
    public enum LicenseTier
    {
        /// <summary>Free / unregistered — 1 camera, no recording, no REST API.</summary>
        Free,

        /// <summary>Pro — unlimited cameras, recording, scheduling, REST API.</summary>
        Pro,

        /// <summary>Enterprise — Pro + multi-seat site license.</summary>
        Enterprise,
    }

    // ══════════════════════════════════════════════════════════════════════════
    // LicenseValidationResult
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Result returned by <c>LicenseService.ValidateAsync</c>.</summary>
    public sealed class LicenseValidationResult
    {
        public bool         IsValid     { get; init; }
        public LicenseInfo? License     { get; init; }
        public string?      ErrorMessage{ get; init; }

        public static LicenseValidationResult Ok(LicenseInfo lic)
            => new() { IsValid = true, License = lic };

        public static LicenseValidationResult Fail(string msg)
            => new() { IsValid = false, ErrorMessage = msg };
    }
}
