using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VisionCore.Core.Update
{
    // ══════════════════════════════════════════════════════════════════════════
    // UpdateManifest — the JSON document your CI publishes alongside each release
    //
    // Host it at a stable URL, e.g.:
    //   https://releases.your-org.com/visioncore/latest.json
    //   https://raw.githubusercontent.com/your-org/visioncore/main/release/latest.json
    //
    // Sign it with a detached .sig file (see UpdateVerifier) and put the
    // public key in your app at build time so the updater can verify before
    // staging anything.
    //
    // Example JSON:
    // {
    //   "version": "1.2.0",
    //   "releaseDate": "2025-09-01T00:00:00Z",
    //   "releaseNotes": "Bug fixes and performance improvements.",
    //   "minOsVersion": "10.0.19041",
    //   "packages": [
    //     {
    //       "kind": "Full",
    //       "url": "https://releases.../VisionCore-1.2.0-win-x64.zip",
    //       "sha256": "abc123...",
    //       "sizeBytes": 52428800,
    //       "signatureUrl": "https://releases.../VisionCore-1.2.0-win-x64.zip.sig"
    //     },
    //     {
    //       "kind": "Delta",
    //       "fromVersion": "1.1.0",
    //       "url": "https://releases.../VisionCore-1.1.0-to-1.2.0-delta.zip",
    //       "sha256": "def456...",
    //       "sizeBytes": 8388608,
    //       "signatureUrl": "https://releases.../VisionCore-1.1.0-to-1.2.0-delta.zip.sig"
    //     }
    //   ],
    //   "isMandatory": false,
    //   "manifestSignature": "base64-encoded-ED25519-signature-over-the-rest-of-this-doc"
    // }
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deserialized form of the update manifest JSON published alongside each release.
    /// </summary>
    public sealed class UpdateManifest
    {
        /// <summary>Semantic version of the new release, e.g. "1.2.0".</summary>
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        /// <summary>UTC date the release was published.</summary>
        [JsonPropertyName("releaseDate")]
        public DateTime ReleaseDate { get; init; }

        /// <summary>Markdown or plain-text release notes shown in the update prompt.</summary>
        [JsonPropertyName("releaseNotes")]
        public string ReleaseNotes { get; init; } = string.Empty;

        /// <summary>
        /// Minimum Windows build required (e.g. "10.0.19041").
        /// Null means no constraint.
        /// </summary>
        [JsonPropertyName("minOsVersion")]
        public string? MinOsVersion { get; init; }

        /// <summary>
        /// If true the updater will not offer a "remind me later" option
        /// and the WPF tray app will nag on every launch.
        /// </summary>
        [JsonPropertyName("isMandatory")]
        public bool IsMandatory { get; init; }

        /// <summary>
        /// One or more download packages.  The updater prefers Delta over Full
        /// when a matching delta from the current version exists.
        /// </summary>
        [JsonPropertyName("packages")]
        public IReadOnlyList<UpdatePackage> Packages { get; init; } =
            Array.Empty<UpdatePackage>();

        /// <summary>
        /// Base64-encoded ED25519 signature over the canonical JSON of this
        /// document (excluding this field).  Verified by UpdateVerifier before
        /// any package is downloaded.
        /// </summary>
        [JsonPropertyName("manifestSignature")]
        public string ManifestSignature { get; init; } = string.Empty;
    }

    /// <summary>A single downloadable update package within a manifest.</summary>
    public sealed class UpdatePackage
    {
        /// <summary>Full = replace everything.  Delta = patch from a specific prior version.</summary>
        [JsonPropertyName("kind")]
        public UpdatePackageKind Kind { get; init; }

        /// <summary>
        /// For Delta packages: the version this delta upgrades FROM.
        /// The updater skips delta packages whose fromVersion != current version.
        /// </summary>
        [JsonPropertyName("fromVersion")]
        public string? FromVersion { get; init; }

        /// <summary>HTTPS URL of the ZIP archive.</summary>
        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;

        /// <summary>Lowercase hex SHA-256 of the ZIP file, verified after download.</summary>
        [JsonPropertyName("sha256")]
        public string Sha256 { get; init; } = string.Empty;

        /// <summary>Download size in bytes — used for progress display and quota checks.</summary>
        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; init; }

        /// <summary>URL of the detached ED25519 signature file (.sig) for this ZIP.</summary>
        [JsonPropertyName("signatureUrl")]
        public string SignatureUrl { get; init; } = string.Empty;
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum UpdatePackageKind { Full, Delta }

    // ── Version wrapper ────────────────────────────────────────────────────────

    /// <summary>
    /// Strongly-typed semantic version parsed from a "major.minor.patch" string.
    /// Implements IComparable so update checks are a simple &gt; comparison.
    /// </summary>
    public sealed class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }

        public AppVersion(int major, int minor, int patch)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
        }

        public static AppVersion Parse(string s)
        {
            var parts = s.TrimStart('v').Split('.');
            if (parts.Length < 3)
                throw new FormatException($"Cannot parse version '{s}'.");
            return new AppVersion(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        }

        public static bool TryParse(string s, out AppVersion? result)
        {
            try { result = Parse(s); return true; }
            catch { result = null; return false; }
        }

        public int CompareTo(AppVersion? other)
        {
            if (other is null) return 1;
            var cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;
            cmp = Minor.CompareTo(other.Minor);
            return cmp != 0 ? cmp : Patch.CompareTo(other.Patch);
        }

        public bool Equals(AppVersion? other) =>
            other is not null && Major == other.Major && Minor == other.Minor && Patch == other.Patch;

        public override bool Equals(object? obj) => Equals(obj as AppVersion);
        public override int  GetHashCode() => HashCode.Combine(Major, Minor, Patch);
        public override string ToString() => $"{Major}.{Minor}.{Patch}";

        public static bool operator >(AppVersion a,  AppVersion b) => a.CompareTo(b) > 0;
        public static bool operator <(AppVersion a,  AppVersion b) => a.CompareTo(b) < 0;
        public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
        public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    }
}
