using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VisionCore.Core.Models;

namespace VisionCore.Core.Services
{
    /// <summary>
    /// Persists <see cref="AppSettings"/> and <see cref="CameraConfig"/> records
    /// in a SQLite database at %LOCALAPPDATA%\VisionCore\data\visioncore.db.
    ///
    /// Tables
    /// ------
    ///   app_settings  — single-row key/value JSON blob
    ///   cameras       — one row per CameraConfig (JSON-serialised)
    /// </summary>
    public sealed class SettingsService
    {
        private readonly ILogger<SettingsService> _logger;
        private readonly string _dbPath;

        private static readonly JsonSerializerOptions JsonOpts =
            new() { PropertyNameCaseInsensitive = true, WriteIndented = false };

        public SettingsService(ILogger<SettingsService> logger)
        {
            _logger = logger;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VisionCore", "data");
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "visioncore.db");
            InitialiseDb();
        }

        // ── Initialise ────────────────────────────────────────────────────────

        private void InitialiseDb()
        {
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS app_settings (
                    id   INTEGER PRIMARY KEY CHECK (id = 1),
                    json TEXT    NOT NULL DEFAULT '{}'
                );
                INSERT OR IGNORE INTO app_settings (id, json) VALUES (1, '{}');

                CREATE TABLE IF NOT EXISTS cameras (
                    id   TEXT PRIMARY KEY,
                    json TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // ── AppSettings ───────────────────────────────────────────────────────

        private AppSettings? _cached;

        public AppSettings App
        {
            get
            {
                if (_cached != null) return _cached;
                using var conn = OpenConnection();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM app_settings WHERE id = 1";
                var json = cmd.ExecuteScalar() as string ?? "{}";
                _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
                return _cached;
            }
        }

        public Task SaveAppSettingsAsync(AppSettings settings)
        {
            _cached = settings;
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "UPDATE app_settings SET json = @json WHERE id = 1";
            cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(settings, JsonOpts));
            cmd.ExecuteNonQuery();
            _logger.LogDebug("AppSettings saved.");
            return Task.CompletedTask;
        }

        // ── Cameras ───────────────────────────────────────────────────────────

        public IReadOnlyList<CameraConfig> Cameras
        {
            get
            {
                var list = new List<CameraConfig>();
                using var conn = OpenConnection();
                using var cmd  = conn.CreateCommand();
                cmd.CommandText = "SELECT json FROM cameras";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var json = reader.GetString(0);
                    var cam  = JsonSerializer.Deserialize<CameraConfig>(json, JsonOpts);
                    if (cam != null) list.Add(cam);
                }
                return list;
            }
        }

        public Task SaveCameraAsync(CameraConfig camera)
        {
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO cameras (id, json) VALUES (@id, @json)
                ON CONFLICT(id) DO UPDATE SET json = excluded.json
                """;
            cmd.Parameters.AddWithValue("@id",   camera.Id.ToString());
            cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(camera, JsonOpts));
            cmd.ExecuteNonQuery();
            _logger.LogDebug("Camera {Id} ({Name}) saved.", camera.Id, camera.Name);
            return Task.CompletedTask;
        }

        public Task DeleteCameraAsync(Guid id)
        {
            using var conn = OpenConnection();
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM cameras WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id.ToString());
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }

        // ── Config export / import ──────────────────────────────────────────

        /// <summary>
        /// Exports application settings and all camera configs to a single
        /// JSON file. Useful for backups or replicating a configuration to
        /// another VisionCore installation (multi-PC deployment).
        ///
        /// Note: the export includes RTSP publish/read credentials and the
        /// REST API token — treat the exported file as sensitive.
        /// </summary>
        public async Task ExportConfigAsync(string path)
        {
            var bundle = new ConfigBundle
            {
                ExportedAtUtc = DateTime.UtcNow,
                AppSettings   = App,
                Cameras       = new List<CameraConfig>(Cameras),
            };

            var json = JsonSerializer.Serialize(bundle,
                new JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(path, json);
            _logger.LogInformation(
                "Config exported to '{Path}' ({Count} camera(s)).", path, bundle.Cameras.Count);
        }

        /// <summary>
        /// Imports application settings and camera configs from a JSON file
        /// previously written by <see cref="ExportConfigAsync"/>.
        ///
        /// Behaviour:
        ///   • AppSettings are replaced wholesale.
        ///   • Cameras are upserted by Id (existing cameras with matching IDs
        ///     are overwritten; new IDs are added). Cameras not present in the
        ///     import are left untouched — use <paramref name="replaceCameras"/>
        ///     to wipe existing cameras first.
        /// </summary>
        /// <param name="path">Path to a JSON file written by ExportConfigAsync.</param>
        /// <param name="replaceCameras">
        /// If true, all existing cameras are deleted before importing.
        /// If false (default), imported cameras are upserted alongside existing ones.
        /// </param>
        /// <returns>Number of cameras imported.</returns>
        public async Task<int> ImportConfigAsync(string path, bool replaceCameras = false)
        {
            var json   = await File.ReadAllTextAsync(path);
            var bundle = JsonSerializer.Deserialize<ConfigBundle>(json, JsonOpts)
                         ?? throw new InvalidDataException("Invalid config file: could not parse JSON.");

            if (bundle.AppSettings != null)
                await SaveAppSettingsAsync(bundle.AppSettings);

            if (replaceCameras)
            {
                foreach (var existing in Cameras)
                    await DeleteCameraAsync(existing.Id);
            }

            int count = 0;
            foreach (var cam in bundle.Cameras ?? new List<CameraConfig>())
            {
                await SaveCameraAsync(cam);
                count++;
            }

            _logger.LogInformation(
                "Config imported from '{Path}' ({Count} camera(s), replaceCameras={Replace}).",
                path, count, replaceCameras);

            return count;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            return conn;
        }
    }

    /// <summary>
    /// Serialisable container for a full VisionCore configuration export —
    /// app-wide settings plus every camera config. Written/read by
    /// <see cref="SettingsService.ExportConfigAsync"/> /
    /// <see cref="SettingsService.ImportConfigAsync"/>.
    /// </summary>
    public sealed class ConfigBundle
    {
        public DateTime ExportedAtUtc { get; set; }
        public AppSettings? AppSettings { get; set; }
        public List<CameraConfig> Cameras { get; set; } = new();
    }
}
