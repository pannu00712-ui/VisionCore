using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Manages license activation, validation, and caching for VisionCore.
    ///
    /// Storage:
    ///   Validated license is encrypted with DPAPI and stored at
    ///   <c>%LOCALAPPDATA%\VisionCore\license.dat</c>.
    ///
    /// Online activation flow:
    ///   1. User enters license key.
    ///   2. <see cref="ActivateAsync"/> POSTs to the activation server.
    ///   3. Server returns a signed <see cref="LicenseInfo"/> JSON blob.
    ///   4. We verify the HMAC signature, check machine-id binding, persist.
    ///
    /// Offline / cached flow:
    ///   On every startup <see cref="LoadCachedAsync"/> reads and decrypts the
    ///   local file.  Signature is re-verified each time so tampering is detected.
    ///
    /// Signature:
    ///   HMAC-SHA256 over the canonical JSON of <see cref="LicenseInfo"/>
    ///   (sorted keys, no whitespace) using the embedded <see cref="SigningKeyBase64"/>.
    ///   Replace <see cref="SigningKeyBase64"/> with your real server key before shipping.
    /// </summary>
    public sealed class LicenseService
    {
        // ── Configuration ─────────────────────────────────────────────────────

        /// <summary>
        /// Base-64–encoded HMAC-SHA256 signing key.
        /// MUST be replaced with the real production key before shipping.
        /// The private key never leaves the server; only the shared HMAC secret is embedded.
        /// </summary>
        private const string SigningKeyBase64 =
            "REPLACE_WITH_REAL_32_BYTE_BASE64_KEY==";

        /// <summary>License activation server endpoint.</summary>
        private const string ActivationUrl =
            "https://license.visioncore.app/api/v1/activate";

        // ── Storage path ──────────────────────────────────────────────────────

        private static readonly string LicensePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisionCore", "license.dat");

        // ── State ─────────────────────────────────────────────────────────────

        private LicenseInfo _current = LicenseInfo.CreateFree();
        private readonly ILogger<LicenseService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        // ── Constructor ───────────────────────────────────────────────────────

        public LicenseService(ILogger<LicenseService> logger)
        {
            _logger = logger;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Currently active license (Free if none activated).</summary>
        public LicenseInfo Current => _current;

        /// <summary>True if the current license is a valid paid license.</summary>
        public bool IsPro => _current.IsPaid && !_current.IsExpired;

        /// <summary>True if local recording is available.</summary>
        public bool CanRecord => _current.RecordingEnabled && !_current.IsExpired;

        /// <summary>True if scheduling is available.</summary>
        public bool CanSchedule => _current.SchedulingEnabled && !_current.IsExpired;

        /// <summary>True if REST API is available.</summary>
        public bool CanUseRestApi => _current.RestApiEnabled && !_current.IsExpired;

        /// <summary>
        /// Maximum cameras allowed. Returns 1 for Free tier, int.MaxValue for Pro+.
        /// </summary>
        public int MaxCameras => _current.MaxCameras < 0 ? int.MaxValue : _current.MaxCameras;

        // ── Startup ───────────────────────────────────────────────────────────

        /// <summary>
        /// Load and validate the cached license on startup.
        /// Falls back to Free silently if no license is found or validation fails.
        /// </summary>
        public async Task LoadCachedAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(LicensePath))
                {
                    _logger.LogInformation("No license file found. Running in Free mode.");
                    return;
                }

                var encrypted = await File.ReadAllBytesAsync(LicensePath);
                var json      = Decrypt(encrypted);

                var wrapper = JsonSerializer.Deserialize<LicensePayload>(json);
                if (wrapper == null)
                {
                    _logger.LogWarning("License file is corrupt.");
                    return;
                }

                var result = Verify(wrapper);
                if (!result.IsValid)
                {
                    _logger.LogWarning("License validation failed: {Reason}", result.ErrorMessage);
                    return;
                }

                _current = result.License!;
                _logger.LogInformation(
                    "License loaded: {Tier} for {Name} (expires {Exp:d}).",
                    _current.TierLabel, _current.CustomerName,
                    _current.ExpiresUtc == DateTime.MaxValue ? "never" : _current.ExpiresUtc.ToShortDateString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cached license.");
            }
            finally
            {
                _lock.Release();
            }
        }

        // ── Activation ────────────────────────────────────────────────────────

        /// <summary>
        /// Activate a license key online.
        /// Returns a <see cref="LicenseValidationResult"/> — check <c>IsValid</c>
        /// and display <c>ErrorMessage</c> to the user if false.
        /// </summary>
        public async Task<LicenseValidationResult> ActivateAsync(
            string licenseKey,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
                return LicenseValidationResult.Fail("License key cannot be empty.");

            await _lock.WaitAsync(ct);
            try
            {
                using var http   = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var request = new
                {
                    LicenseKey = licenseKey.Trim(),
                    MachineId  = GetMachineId(),
                    AppVersion = GetAppVersion(),
                };

                HttpResponseMessage resp;
                try
                {
                    resp = await http.PostAsJsonAsync(ActivationUrl, request, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Activation server unreachable.");
                    return LicenseValidationResult.Fail(
                        "Could not reach the activation server. Check your internet connection.");
                }

                if (resp.StatusCode == HttpStatusCode.NotFound ||
                    resp.StatusCode == HttpStatusCode.BadRequest)
                {
                    var errBody = await resp.Content.ReadAsStringAsync(ct);
                    return LicenseValidationResult.Fail($"Invalid license key: {errBody}");
                }

                if (!resp.IsSuccessStatusCode)
                    return LicenseValidationResult.Fail(
                        $"Activation server error ({(int)resp.StatusCode}).");

                var wrapper = await resp.Content.ReadFromJsonAsync<LicensePayload>(
                    cancellationToken: ct);
                if (wrapper == null)
                    return LicenseValidationResult.Fail("Malformed server response.");

                var result = Verify(wrapper);
                if (!result.IsValid) return result;

                // Persist
                _current = result.License!;
                await PersistAsync(wrapper);

                _logger.LogInformation(
                    "License activated: {Tier} for {Name}.",
                    _current.TierLabel, _current.CustomerName);

                return result;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Remove the local license file and revert to Free.</summary>
        public async Task DeactivateAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (File.Exists(LicensePath))
                    File.Delete(LicensePath);
                _current = LicenseInfo.CreateFree();
                _logger.LogInformation("License deactivated. Running in Free mode.");
            }
            finally
            {
                _lock.Release();
            }
        }

        // ── Machine ID ────────────────────────────────────────────────────────

        /// <summary>
        /// Stable hardware fingerprint derived from the Windows machine SID.
        /// Falls back to a random GUID persisted in %LOCALAPPDATA% if WMI is unavailable.
        /// </summary>
        public static string GetMachineId()
        {
            try
            {
                // Use the Windows machine GUID from the registry (stable, no WMI needed)
                var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (key != null)
                {
                    var guid = key.GetValue("MachineGuid") as string;
                    if (!string.IsNullOrEmpty(guid)) return guid;
                }
            }
            catch { /* fall through */ }

            // Fallback: persist a random ID
            var fallbackPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VisionCore", "machine.id");

            if (File.Exists(fallbackPath))
                return File.ReadAllText(fallbackPath).Trim();

            var newId = Guid.NewGuid().ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
            File.WriteAllText(fallbackPath, newId);
            return newId;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static LicenseValidationResult Verify(LicensePayload wrapper)
        {
            // 1. Verify HMAC signature
            try
            {
                var key       = Convert.FromBase64String(SigningKeyBase64);
                var canonical = Encoding.UTF8.GetBytes(wrapper.Data);
                using var hmac = new HMACSHA256(key);
                var computed   = Convert.ToBase64String(hmac.ComputeHash(canonical));
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(computed),
                        Encoding.UTF8.GetBytes(wrapper.Signature ?? "")))
                {
                    return LicenseValidationResult.Fail("License signature is invalid.");
                }
            }
            catch
            {
                return LicenseValidationResult.Fail("License signature verification failed.");
            }

            // 2. Deserialise
            LicenseInfo? lic;
            try
            {
                lic = JsonSerializer.Deserialize<LicenseInfo>(wrapper.Data,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return LicenseValidationResult.Fail("License data is malformed.");
            }

            if (lic == null)
                return LicenseValidationResult.Fail("License data is empty.");

            // 3. Expiry
            if (lic.IsExpired)
                return LicenseValidationResult.Fail(
                    $"License expired on {lic.ExpiresUtc:d}.");

            // 4. Machine binding
            if (!string.IsNullOrEmpty(lic.MachineId) &&
                lic.MachineId != GetMachineId())
            {
                return LicenseValidationResult.Fail(
                    "License is bound to a different machine.");
            }

            return LicenseValidationResult.Ok(lic);
        }

        private static async Task PersistAsync(LicensePayload wrapper)
        {
            var json      = JsonSerializer.Serialize(wrapper);
            var encrypted = Encrypt(json);
            Directory.CreateDirectory(Path.GetDirectoryName(LicensePath)!);
            await File.WriteAllBytesAsync(LicensePath, encrypted);
        }

        // DPAPI encryption — protects against casual file tampering on the same machine.
        private static byte[] Encrypt(string plaintext)
            => ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext),
                null,
                DataProtectionScope.CurrentUser);

        private static string Decrypt(byte[] ciphertext)
            => Encoding.UTF8.GetString(
                ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser));

        private static string GetAppVersion()
            => System.Reflection.Assembly.GetEntryAssembly()
                   ?.GetName().Version?.ToString() ?? "1.0.0";

        // ── Inner types ───────────────────────────────────────────────────────

        /// <summary>Wire format returned by the activation server and stored in license.dat.</summary>
        private sealed class LicensePayload
        {
            /// <summary>Canonical JSON of <see cref="LicenseInfo"/>.</summary>
            public string  Data      { get; set; } = string.Empty;

            /// <summary>Base-64 HMAC-SHA256 signature of <see cref="Data"/>.</summary>
            public string? Signature { get; set; }
        }
    }
}
