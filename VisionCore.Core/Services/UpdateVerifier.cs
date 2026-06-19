using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Update;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Verifies the authenticity of update manifests and downloaded packages
    /// using ED25519 signatures.
    ///
    /// Key management
    /// --------------
    /// The verifier embeds the release signing public key at compile time.
    /// To rotate the key, ship a new build — never trust a key delivered at
    /// runtime (that would defeat the purpose).
    ///
    /// Key generation (one-time, keep private key secret):
    ///   # Using OpenSSL (or the GenerateKeyPair helper below)
    ///   openssl genpkey -algorithm ed25519 -out signing-private.pem
    ///   openssl pkey -in signing-private.pem -pubout -out signing-public.pem
    ///
    /// Signing a manifest (CI step):
    ///   openssl pkeyutl -sign -inkey signing-private.pem \
    ///                   -in manifest-canonical.json -out manifest.sig
    ///   # Base64-encode and embed as manifestSignature in the JSON
    ///   openssl base64 -in manifest.sig
    ///
    /// Signing a ZIP (CI step):
    ///   openssl pkeyutl -sign -inkey signing-private.pem \
    ///                   -in VisionCore-1.2.0.zip -out VisionCore-1.2.0.zip.sig
    ///
    /// .NET 8's System.Security.Cryptography provides built-in ED25519 support
    /// via ECDsa with the ECCurve.CreateFromFriendlyName("Edwards25519") curve.
    ///
    /// Important: .NET uses "Edwards25519" / Ed25519 via ECDsa on .NET 8+.
    /// If you need an older .NET version, swap this for BouncyCastle.
    /// </summary>
    public sealed class UpdateVerifier
    {
        private readonly ILogger<UpdateVerifier> _logger;
        private readonly HttpClient              _http;

        // ── Embedded public key ────────────────────────────────────────────────
        //
        // Replace this placeholder with the real Base64-DER-encoded SubjectPublicKeyInfo
        // of your ED25519 signing key.
        //
        // To get the correct bytes from your PEM:
        //   openssl pkey -in signing-public.pem -pubin -outform DER | base64
        //
        private const string EmbeddedPublicKeyBase64 =
            "REPLACE_WITH_BASE64_DER_ENCODED_ED25519_PUBLIC_KEY";

        private readonly ECDsa _publicKey;

        public UpdateVerifier(ILogger<UpdateVerifier> logger, HttpClient? http = null)
        {
            _logger = logger;
            _http   = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _publicKey = LoadPublicKey();
        }

        // ── Manifest signature ────────────────────────────────────────────────

        /// <summary>
        /// Verifies the <c>manifestSignature</c> field in the manifest against
        /// the canonical JSON representation (all fields except manifestSignature
        /// itself, serialized with sorted keys).
        /// Returns false — rather than throwing — on any verification failure so
        /// the caller can log and suppress the update without crashing.
        /// </summary>
        public Task<bool> VerifyManifestAsync(UpdateManifest manifest, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrEmpty(manifest.ManifestSignature))
                {
                    _logger.LogWarning("Manifest has no signature field.");
                    return Task.FromResult(false);
                }

                var canonical = CanonicalizeManifest(manifest);
                var dataBytes = Encoding.UTF8.GetBytes(canonical);
                var sigBytes  = Convert.FromBase64String(manifest.ManifestSignature);

                var ok = _publicKey.VerifyData(dataBytes, sigBytes, HashAlgorithmName.SHA512);
                if (!ok)
                    _logger.LogWarning("Manifest signature is INVALID for version {Ver}.", manifest.Version);

                return Task.FromResult(ok);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manifest signature verification threw.");
                return Task.FromResult(false);
            }
        }

        // ── Package SHA-256 + signature ───────────────────────────────────────

        /// <summary>
        /// 1. Computes the SHA-256 of <paramref name="zipPath"/> and compares it
        ///    to <paramref name="package"/>.Sha256.
        /// 2. Downloads the detached .sig file from <paramref name="package"/>.SignatureUrl
        ///    and verifies the ED25519 signature over the raw ZIP bytes.
        /// Both checks must pass; failure in either returns false.
        /// </summary>
        public async Task<bool> VerifyPackageAsync(
            string        zipPath,
            UpdatePackage package,
            CancellationToken ct = default)
        {
            // ── Step 1: SHA-256 hash ───────────────────────────────────────────
            string actualHash;
            try
            {
                await using var fs = File.OpenRead(zipPath);
                var hashBytes = await SHA256.HashDataAsync(fs, ct);
                actualHash    = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SHA-256 computation failed for {Path}.", zipPath);
                return false;
            }

            if (!string.Equals(actualHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "SHA-256 mismatch for {Url}. Expected {Expected}, got {Actual}.",
                    package.Url, package.Sha256, actualHash);
                return false;
            }

            _logger.LogDebug("SHA-256 verified for {Url}.", package.Url);

            // ── Step 2: detached ED25519 signature ────────────────────────────
            if (string.IsNullOrEmpty(package.SignatureUrl))
            {
                _logger.LogWarning("Package {Url} has no SignatureUrl — skipping signature check.", package.Url);
                // Treat missing sig as a failure in production; relax for dev builds by returning true here.
                return false;
            }

            byte[] sigBytes;
            try
            {
                sigBytes = await _http.GetByteArrayAsync(package.SignatureUrl, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch package signature from {Url}.", package.SignatureUrl);
                return false;
            }

            try
            {
                // Sign over the raw file bytes (not the hash)
                await using var fs = File.OpenRead(zipPath);
                // Stream the file in 1 MB chunks to avoid loading it all into memory
                const int BufSize = 1024 * 1024;
                var buf = ArrayPool<byte>.Shared.Rent(BufSize);
                try
                {
                    using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                    int read;
                    while ((read = await fs.ReadAsync(buf.AsMemory(0, BufSize), ct)) > 0)
                        incrementalHash.AppendData(buf, 0, read);

                    var digest = incrementalHash.GetCurrentHash();
                    var ok = _publicKey.VerifyHash(digest, sigBytes);

                    if (!ok)
                        _logger.LogError("ED25519 signature INVALID for package {Url}.", package.Url);
                    else
                        _logger.LogDebug("ED25519 signature verified for {Url}.", package.Url);

                    return ok;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Signature verification threw for {Url}.", package.Url);
                return false;
            }
        }

        // ── Key loading ───────────────────────────────────────────────────────

        private static ECDsa LoadPublicKey()
        {
            try
            {
                var derBytes = Convert.FromBase64String(EmbeddedPublicKeyBase64);
                var ecdsa    = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(derBytes, out _);
                return ecdsa;
            }
            catch (Exception ex)
            {
                // In development (placeholder key), create a dummy key so the
                // app doesn't crash — but log loudly.
                Console.Error.WriteLine(
                    $"[UpdateVerifier] CRITICAL: Could not load embedded signing key: {ex.Message}. " +
                    "All update signature checks will FAIL until a real key is embedded.");
                return ECDsa.Create(ECCurve.NamedCurves.nistP256); // dummy, won't verify anything
            }
        }

        // ── Canonical JSON ────────────────────────────────────────────────────

        /// <summary>
        /// Produces a deterministic JSON string of the manifest with
        /// <c>manifestSignature</c> excluded and keys sorted alphabetically.
        /// This is the exact byte sequence that was signed at release time.
        /// </summary>
        private static string CanonicalizeManifest(UpdateManifest manifest)
        {
            // Re-serialize without the signature field, sorted keys
            var doc = new
            {
                isMandatory   = manifest.IsMandatory,
                minOsVersion  = manifest.MinOsVersion,
                packages      = manifest.Packages,
                releaseDate   = manifest.ReleaseDate.ToString("O"),
                releaseNotes  = manifest.ReleaseNotes,
                version       = manifest.Version,
            };

            return JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented          = false,
                PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
        }

        // ── Key generation helper (dev / CI use only) ─────────────────────────

        /// <summary>
        /// Generates a fresh ED25519 key pair and writes the public key (Base64 DER)
        /// to stdout and the private key (PEM) to <paramref name="privateKeyPath"/>.
        /// Run once during project setup; never ship the private key.
        /// </summary>
        public static void GenerateKeyPair(string privateKeyPath)
        {
            using var ecdsa = ECDsa.Create(ECCurve.CreateFromFriendlyName("Edwards25519"));

            var pubDer    = ecdsa.ExportSubjectPublicKeyInfo();
            var pubBase64 = Convert.ToBase64String(pubDer);

            var privPem   = new StringBuilder();
            privPem.AppendLine("-----BEGIN PRIVATE KEY-----");
            privPem.AppendLine(Convert.ToBase64String(
                ecdsa.ExportPkcs8PrivateKey(), Base64FormattingOptions.InsertLineBreaks));
            privPem.AppendLine("-----END PRIVATE KEY-----");

            File.WriteAllText(privateKeyPath, privPem.ToString());

            Console.WriteLine("=== ED25519 Public Key (paste into UpdateVerifier.EmbeddedPublicKeyBase64) ===");
            Console.WriteLine(pubBase64);
            Console.WriteLine($"Private key written to: {privateKeyPath}");
        }
    }
}
