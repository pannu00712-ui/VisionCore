using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using VisionCore.Core.Models;
using VisionCore.Core.Services;

namespace VisionCore.Core.Services
{
    // ══════════════════════════════════════════════════════════════════════════
    // REST API Service
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Embeds a full ASP.NET Core Minimal API server inside the WPF app.
    ///
    /// Default: http://0.0.0.0:7880/api/v1
    /// Swagger:  http://localhost:7880/swagger
    ///
    /// Auth:
    ///   POST /api/v1/auth/token  →  returns JWT Bearer token
    ///   All other endpoints require:  Authorization: Bearer {token}
    ///
    /// Endpoints:
    ///   GET    /api/v1/status
    ///   GET    /api/v1/cameras
    ///   GET    /api/v1/cameras/{id}
    ///   POST   /api/v1/cameras
    ///   PUT    /api/v1/cameras/{id}
    ///   DELETE /api/v1/cameras/{id}
    ///   POST   /api/v1/cameras/{id}/start
    ///   POST   /api/v1/cameras/{id}/stop
    ///   GET    /api/v1/cameras/{id}/stats
    ///   GET    /api/v1/logs
    ///   POST   /api/v1/auth/token
    /// </summary>
    public sealed class RestApiService : IDisposable
    {
        private readonly ILogger<RestApiService>  _logger;
        private readonly SettingsService          _settings;
        private readonly CameraManager            _cameras;
        private WebApplication?                   _app;
        private CancellationTokenSource?          _cts;

        // In-memory log ring buffer (newest first, max 500 entries)
        private readonly LinkedList<LogEntry> _logBuffer = new();
        private readonly object _logLock = new();
        private const int MaxLogEntries = 500;

        public bool IsRunning { get; private set; }

        public RestApiService(
            ILogger<RestApiService> logger,
            SettingsService settings,
            CameraManager cameras)
        {
            _logger   = logger;
            _settings = settings;
            _cameras  = cameras;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public async Task StartAsync(CancellationToken ct = default)
        {
            if (!_settings.App.RestApiEnabled)
            {
                _logger.LogInformation("REST API is disabled in settings.");
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var port   = _settings.App.RestApiPort;
            var secret = _settings.App.RestApiToken;

            if (string.IsNullOrEmpty(secret))
            {
                secret = GenerateSecret();
                _settings.App.RestApiToken = secret;
                await _settings.SaveAppSettingsAsync(_settings.App);
                _logger.LogWarning("REST API token was empty — generated new token: {Token}", secret);
            }

            var builder = WebApplication.CreateBuilder();

            // ── Services ───────────────────────────────────────────────────
            builder.Services.AddSingleton(this);
            builder.Services.AddSingleton(_settings);
            builder.Services.AddSingleton(_cameras);

            // JWT auth
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(opts =>
                {
                    opts.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                        ValidateIssuer           = true,
                        ValidIssuer              = "VisionCore",
                        ValidateAudience         = true,
                        ValidAudience            = "VisionCoreApi",
                        ValidateLifetime         = true,
                        ClockSkew                = TimeSpan.FromMinutes(1),
                    };
                });

            builder.Services.AddAuthorization();

            // Swagger / OpenAPI
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new()
                {
                    Title       = "VisionCore REST API",
                    Version     = "v1",
                    Description = "Remote control API for VisionCore Virtual IP Camera"
                });
                c.AddSecurityDefinition("Bearer", new()
                {
                    Type        = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                    Scheme      = "bearer",
                    BearerFormat = "JWT",
                    Description = "Paste your JWT token here."
                });
                c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
                {
                    {
                        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                        {
                            Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
                        },
                        Array.Empty<string>()
                    }
                });
            });

            // CORS — allow dashboard / remote tools
            builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
                p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

            builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
            builder.Logging.ClearProviders(); // use Serilog from host

            _app = builder.Build();

            _app.UseSwagger();
            _app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "VisionCore API v1");
                c.RoutePrefix = "swagger";
            });

            _app.UseCors();
            _app.UseAuthentication();
            _app.UseAuthorization();

            // ── Register all endpoints ─────────────────────────────────────
            RegisterEndpoints(_app, secret);

            IsRunning = true;
            _ = _app.RunAsync(_cts.Token).ContinueWith(_ => IsRunning = false);

            _logger.LogInformation(
                "REST API started on http://0.0.0.0:{Port}/api/v1  |  Swagger: http://localhost:{Port}/swagger",
                port, port);
        }

        public async Task StopAsync()
        {
            IsRunning = false;
            _cts?.Cancel();
            if (_app != null)
                await _app.StopAsync();
            _logger.LogInformation("REST API stopped.");
        }

        // ── Log buffer (called by Serilog sink or manually) ───────────────────

        public void AddLog(LogEntry entry)
        {
            lock (_logLock)
            {
                _logBuffer.AddFirst(entry);
                while (_logBuffer.Count > MaxLogEntries)
                    _logBuffer.RemoveLast();
            }
        }

        public IReadOnlyList<LogEntry> GetLogs(int count = 100)
        {
            lock (_logLock)
                return _logBuffer.Take(Math.Min(count, MaxLogEntries)).ToList();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Endpoint registration
        // ══════════════════════════════════════════════════════════════════════

        private void RegisterEndpoints(WebApplication app, string secret)
        {
            const string prefix = "/api/v1";

            // ── Auth ───────────────────────────────────────────────────────

            /// <summary>Get a JWT token. Body: { "username": "admin", "password": "..." }</summary>
            app.MapPost($"{prefix}/auth/token", (
                [FromBody] LoginRequest req,
                SettingsService         settings) =>
            {
                // Validate against configured admin users
                var validUser = settings.App.AdminUsers.Contains(req.Username);
                var validPass = req.Password == settings.App.RestApiToken;

                if (!validUser || !validPass)
                    return Results.Unauthorized();

                var token = GenerateJwt(req.Username, secret);
                return Results.Ok(new
                {
                    token,
                    expires    = DateTime.UtcNow.AddHours(8),
                    token_type = "Bearer"
                });
            })
            .AllowAnonymous()
            .WithName("Login")
            .WithSummary("Get JWT token")
            .WithTags("Auth")
            .Produces<object>(200)
            .Produces(401);

            // ── Status ─────────────────────────────────────────────────────

            /// <summary>Server health check + aggregate stats.</summary>
            app.MapGet($"{prefix}/status", (
                CameraManager cameras,
                SettingsService settings) =>
            {
                var allStats = cameras.GetAllStats();
                return Results.Ok(new
                {
                    status           = "ok",
                    version          = "1.0.0",
                    uptime           = FormatUptime(),
                    total_cameras    = settings.Cameras.Count,
                    active_cameras   = allStats.Count(s => s.Value.IsRunning),
                    total_clients    = cameras.TotalConnections,
                    total_bitrate_kbps = allStats.Values.Sum(s => s.CurrentBitrateKbps),
                    timestamp        = DateTime.UtcNow,
                });
            })
            .RequireAuthorization()
            .WithName("GetStatus")
            .WithSummary("Server health and aggregate stats")
            .WithTags("System")
            .Produces<object>(200);

            // ── Cameras — CRUD ─────────────────────────────────────────────

            /// <summary>List all configured cameras with their live stats.</summary>
            app.MapGet($"{prefix}/cameras", (
                CameraManager   cameras,
                SettingsService settings) =>
            {
                var result = settings.Cameras.Select(c =>
                {
                    var stats = cameras.GetStats(c.Id);
                    return MapCameraResponse(c, stats);
                });
                return Results.Ok(result);
            })
            .RequireAuthorization()
            .WithName("GetCameras")
            .WithSummary("List all cameras")
            .WithTags("Cameras")
            .Produces<IEnumerable<object>>(200);

            /// <summary>Get a single camera by ID.</summary>
            app.MapGet($"{prefix}/cameras/{{id:guid}}", (
                Guid            id,
                CameraManager   cameras,
                SettingsService settings) =>
            {
                var cam = settings.Cameras.FirstOrDefault(c => c.Id == id);
                if (cam == null) return Results.NotFound(new { error = $"Camera {id} not found." });
                var stats = cameras.GetStats(id);
                return Results.Ok(MapCameraResponse(cam, stats));
            })
            .RequireAuthorization()
            .WithName("GetCamera")
            .WithSummary("Get camera by ID")
            .WithTags("Cameras")
            .Produces<object>(200)
            .Produces(404);

            /// <summary>Add a new virtual camera.</summary>
            app.MapPost($"{prefix}/cameras", async (
                [FromBody] CameraConfig   body,
                CameraManager             cameras,
                SettingsService           settings) =>
            {
                // Assign new ID to prevent client from spoofing
                body.Id = Guid.NewGuid();
                await settings.SaveCameraAsync(body);
                _logger.LogInformation("REST API: Camera '{Name}' created.", body.Name);
                return Results.Created($"/api/v1/cameras/{body.Id}",
                    MapCameraResponse(body, null));
            })
            .RequireAuthorization()
            .WithName("CreateCamera")
            .WithSummary("Create a new virtual camera")
            .WithTags("Cameras")
            .Produces<object>(201)
            .Produces(400);

            /// <summary>Update an existing camera (stops stream if running).</summary>
            app.MapPut($"{prefix}/cameras/{{id:guid}}", async (
                Guid            id,
                [FromBody] CameraConfig body,
                CameraManager   cameras,
                SettingsService settings) =>
            {
                var existing = settings.Cameras.FirstOrDefault(c => c.Id == id);
                if (existing == null)
                    return Results.NotFound(new { error = $"Camera {id} not found." });

                // Stop if running before updating
                if (cameras.IsRunning(id))
                {
                    await cameras.StopCameraAsync(id);
                    _logger.LogInformation("REST API: Stopped camera {Id} for update.", id);
                }

                body.Id = id; // enforce correct ID
                await settings.SaveCameraAsync(body);
                _logger.LogInformation("REST API: Camera '{Name}' updated.", body.Name);
                return Results.Ok(MapCameraResponse(body, null));
            })
            .RequireAuthorization()
            .WithName("UpdateCamera")
            .WithSummary("Update a camera (stops stream if active)")
            .WithTags("Cameras")
            .Produces<object>(200)
            .Produces(404);

            /// <summary>Delete a camera and stop its stream.</summary>
            app.MapDelete($"{prefix}/cameras/{{id:guid}}", async (
                Guid            id,
                CameraManager   cameras,
                SettingsService settings) =>
            {
                var existing = settings.Cameras.FirstOrDefault(c => c.Id == id);
                if (existing == null)
                    return Results.NotFound(new { error = $"Camera {id} not found." });

                if (cameras.IsRunning(id))
                    await cameras.StopCameraAsync(id);

                await settings.DeleteCameraAsync(id);
                _logger.LogInformation("REST API: Camera {Id} deleted.", id);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithName("DeleteCamera")
            .WithSummary("Delete a camera")
            .WithTags("Cameras")
            .Produces(204)
            .Produces(404);

            // ── Stream control ─────────────────────────────────────────────

            /// <summary>Start streaming a camera.</summary>
            app.MapPost($"{prefix}/cameras/{{id:guid}}/start", async (
                Guid            id,
                CameraManager   cameras,
                SettingsService settings) =>
            {
                var cam = settings.Cameras.FirstOrDefault(c => c.Id == id);
                if (cam == null)
                    return Results.NotFound(new { error = $"Camera {id} not found." });

                if (cameras.IsRunning(id))
                    return Results.Conflict(new { error = "Camera is already streaming." });

                try
                {
                    await cameras.StartCameraAsync(cam);
                    _logger.LogInformation("REST API: Camera '{Name}' started.", cam.Name);
                    return Results.Ok(new
                    {
                        message  = "Stream started.",
                        rtsp_url = RtspStreamManager.GetClientUrl(cam),
                        onvif    = $"http://{{host}}:{cam.OnvifPort}/onvif/device_service"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "REST API: Failed to start camera {Id}.", id);
                    return Results.Problem(ex.Message, statusCode: 500);
                }
            })
            .RequireAuthorization()
            .WithName("StartCamera")
            .WithSummary("Start a camera stream")
            .WithTags("Cameras")
            .Produces<object>(200)
            .Produces(404)
            .Produces(409);

            /// <summary>Stop a streaming camera.</summary>
            app.MapPost($"{prefix}/cameras/{{id:guid}}/stop", async (
                Guid            id,
                CameraManager   cameras,
                SettingsService settings) =>
            {
                var cam = settings.Cameras.FirstOrDefault(c => c.Id == id);
                if (cam == null)
                    return Results.NotFound(new { error = $"Camera {id} not found." });

                if (!cameras.IsRunning(id))
                    return Results.Conflict(new { error = "Camera is not streaming." });

                await cameras.StopCameraAsync(id);
                _logger.LogInformation("REST API: Camera '{Name}' stopped.", cam.Name);
                return Results.Ok(new { message = "Stream stopped." });
            })
            .RequireAuthorization()
            .WithName("StopCamera")
            .WithSummary("Stop a camera stream")
            .WithTags("Cameras")
            .Produces<object>(200)
            .Produces(404)
            .Produces(409);

            // ── Live stats ─────────────────────────────────────────────────

            /// <summary>Get live stats for a single camera (bitrate, fps, clients, uptime).</summary>
            app.MapGet($"{prefix}/cameras/{{id:guid}}/stats", (
                Guid          id,
                CameraManager cameras) =>
            {
                var stats = cameras.GetStats(id);
                if (stats == null)
                    return Results.NotFound(new { error = $"Camera {id} not found or not running." });

                return Results.Ok(new
                {
                    camera_id          = stats.CameraId,
                    is_running         = stats.IsRunning,
                    active_clients     = stats.ActiveClients,
                    bitrate_kbps       = Math.Round(stats.CurrentBitrateKbps, 1),
                    fps                = Math.Round(stats.Fps, 1),
                    frames_encoded     = stats.FramesEncoded,
                    bytes_sent         = stats.BytesSent,
                    uptime_seconds     = (long)stats.Uptime.TotalSeconds,
                    uptime_human       = stats.Uptime.ToString(@"hh\:mm\:ss"),
                    motion_detected    = stats.MotionDetected,
                    gpu_encoder        = stats.GpuEncoder,
                    cpu_usage_pct      = Math.Round(stats.CpuUsage, 1),
                    timestamp          = DateTime.UtcNow,
                });
            })
            .RequireAuthorization()
            .WithName("GetCameraStats")
            .WithSummary("Live stats for a camera")
            .WithTags("Cameras")
            .Produces<object>(200)
            .Produces(404);

            /// <summary>Capture a single JPEG snapshot from a running camera's stream.</summary>
            app.MapGet($"{prefix}/cameras/{{id:guid}}/snapshot", async (
                Guid            id,
                CameraManager   cameras,
                SettingsService settings,
                CancellationToken ct) =>
            {
                var cam = settings.Cameras.FirstOrDefault(c => c.Id == id);
                if (cam == null)
                    return Results.NotFound(new { error = $"Camera {id} not found." });

                if (!cameras.IsRunning(id))
                    return Results.Conflict(new { error = "Camera is not streaming." });

                var jpeg = await cameras.CaptureSnapshotAsync(id, ct);
                if (jpeg == null)
                    return Results.Problem(
                        detail: "Snapshot capture failed or timed out.",
                        statusCode: 500);

                return Results.Bytes(jpeg, "image/jpeg");
            })
            .RequireAuthorization()
            .WithName("GetCameraSnapshot")
            .WithSummary("Capture a single JPEG snapshot from a camera's live stream")
            .WithTags("Cameras")
            .Produces(200, contentType: "image/jpeg")
            .Produces(404)
            .Produces(409)
            .Produces(500);

            // ── Bulk control ───────────────────────────────────────────────

            /// <summary>Start all cameras at once.</summary>
            app.MapPost($"{prefix}/cameras/start-all", async (
                CameraManager   cameras,
                SettingsService settings) =>
            {
                var results = new List<object>();
                foreach (var cam in settings.Cameras)
                {
                    if (cameras.IsRunning(cam.Id))
                    {
                        results.Add(new { id = cam.Id, name = cam.Name, status = "already_running" });
                        continue;
                    }
                    try
                    {
                        await cameras.StartCameraAsync(cam);
                        results.Add(new { id = cam.Id, name = cam.Name, status = "started" });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { id = cam.Id, name = cam.Name, status = "error", error = ex.Message });
                    }
                }
                return Results.Ok(new { results });
            })
            .RequireAuthorization()
            .WithName("StartAllCameras")
            .WithSummary("Start all cameras")
            .WithTags("Cameras")
            .Produces<object>(200);

            /// <summary>Stop all cameras at once.</summary>
            app.MapPost($"{prefix}/cameras/stop-all", async (
                CameraManager cameras,
                SettingsService settings) =>
            {
                await cameras.StopAllAsync();
                return Results.Ok(new { message = $"All cameras stopped." });
            })
            .RequireAuthorization()
            .WithName("StopAllCameras")
            .WithSummary("Stop all cameras")
            .WithTags("Cameras")
            .Produces<object>(200);

            // ── App settings ───────────────────────────────────────────────

            /// <summary>Get current app settings (token is redacted).</summary>

            // ── Manual ONVIF Motion Trigger ────────────────────────────────────────
            //
            //  POST /api/v1/cameras/{id}/trigger-motion
            //  Body: { "active": true, "durationSeconds": 5 }   (durationSeconds optional)
            //
            //  Also registers the ONVIF-style path for drop-in DeskCamera compatibility:
            //  POST /onvif/event_service/api/TriggerEvent
            //  Body: { "cameraId": "guid", "active": true }

            app.MapPost($"{prefix}/cameras/{{id:guid}}/trigger-motion", async (
                Guid            id,
                TriggerMotionRequest req,
                OnvifServer     onvif,
                MotionDetector  motion,
                InputActivityMonitor inputMon) =>
            {
                onvif.NotifyMotion(id, req.Active);
                motion.TriggerInputMotion(id, req.Active);

                if (req.Active && req.DurationSeconds.HasValue && req.DurationSeconds > 0)
                {
                    // Auto-clear after the specified duration
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(req.DurationSeconds.Value));
                        onvif.NotifyMotion(id, false);
                        motion.TriggerInputMotion(id, false);
                    });
                }

                return Results.Ok(new
                {
                    cameraId  = id,
                    active    = req.Active,
                    duration  = req.DurationSeconds,
                    timestamp = DateTime.UtcNow,
                });
            })
            .RequireAuthorization()
            .WithName("TriggerMotion");

            // DeskCamera-compatible path (no auth — matches DeskCamera behavior)
            app.MapPost("/onvif/event_service/api/TriggerEvent", (
                OnvifTriggerRequest req,
                OnvifServer  onvif,
                MotionDetector motion) =>
            {
                if (req.CameraId == Guid.Empty)
                    return Results.BadRequest(new { error = "cameraId required" });

                onvif.NotifyMotion(req.CameraId, req.Active);
                motion.TriggerInputMotion(req.CameraId, req.Active);

                return Results.Ok(new
                {
                    status    = "ok",
                    cameraId  = req.CameraId,
                    active    = req.Active,
                    timestamp = DateTime.UtcNow,
                });
            })
            .WithName("OnvifTriggerEvent");

            app.MapGet($"{prefix}/settings", (SettingsService settings) =>
            {
                var s = settings.App;
                return Results.Ok(new
                {
                    theme                = s.Theme,
                    start_with_windows   = s.StartWithWindows,
                    run_as_service       = s.RunAsService,
                    minimize_to_tray     = s.MinimizeToTray,
                    rest_api_enabled     = s.RestApiEnabled,
                    rest_api_port        = s.RestApiPort,
                    rest_api_token       = "[REDACTED]",
                    log_level            = s.LogLevel,
                });
            })
            .RequireAuthorization()
            .WithName("GetSettings")
            .WithSummary("Get app settings (token redacted)")
            .WithTags("System")
            .Produces<object>(200);

            // ── Logs ───────────────────────────────────────────────────────

            /// <summary>Get recent log entries. ?count=100 (max 500)</summary>
            app.MapGet($"{prefix}/logs", (
                HttpRequest     request,
                RestApiService  api) =>
            {
                var count = 100;
                if (request.Query.TryGetValue("count", out var cv) &&
                    int.TryParse(cv, out var parsed))
                    count = Math.Clamp(parsed, 1, 500);

                var logs = api.GetLogs(count).Select(l => new
                {
                    timestamp = l.Timestamp,
                    level     = l.Level,
                    source    = l.SourceContext,
                    message   = l.Message,
                });
                return Results.Ok(logs);
            })
            .RequireAuthorization()
            .WithName("GetLogs")
            .WithSummary("Recent log entries (?count=N, max 500)")
            .WithTags("System")
            .Produces<IEnumerable<object>>(200);

            // ── Health (no auth — for load balancers / uptime monitors) ───

            app.MapGet("/health", () => Results.Ok(new { status = "healthy", ts = DateTime.UtcNow }))
               .AllowAnonymous()
               .ExcludeFromDescription();

            // ── 404 catch-all ──────────────────────────────────────────────

            app.MapFallback(() => Results.NotFound(new
            {
                error    = "Endpoint not found.",
                docs     = $"http://localhost:{_settings.App.RestApiPort}/swagger"
            }));
        }

        // ══════════════════════════════════════════════════════════════════════
        // JWT helpers
        // ══════════════════════════════════════════════════════════════════════

        private static string GenerateJwt(string username, string secret)
        {
            var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.Name,               username),
                new Claim(ClaimTypes.Role,               "Admin"),
                new Claim(JwtRegisteredClaimNames.Sub,   username),
                new Claim(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Iss,   "VisionCore"),
                new Claim(JwtRegisteredClaimNames.Aud,   "VisionCoreApi"),
            };

            var token = new JwtSecurityToken(
                claims:   claims,
                expires:  DateTime.UtcNow.AddHours(8),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Response shape helpers
        // ══════════════════════════════════════════════════════════════════════

        private static object MapCameraResponse(CameraConfig cam, CameraStats? stats) => new
        {
            id             = cam.Id,
            name           = cam.Name,
            enabled        = cam.Enabled,
            source         = cam.Source.ToString(),
            codec          = cam.Codec.ToString(),
            resolution     = cam.Resolution.ToString(),
            frame_rate     = cam.FrameRate,
            bitrate_kbps   = cam.Bitrate,
            gpu_accel      = cam.GpuAccel.ToString(),
            audio_source   = cam.AudioSource.ToString(),
            rtsp_port      = cam.RtspPort,
            rtsp_path      = cam.RtspPath,
            rtsp_url       = RtspStreamManager.GetClientUrl(cam),
            onvif_enabled  = cam.OnvifEnabled,
            onvif_port     = cam.OnvifPort,
            motion_detection = cam.MotionDetection,
            overlays_count = cam.Overlays.Count,
            is_running     = stats?.IsRunning ?? false,
            active_clients = stats?.ActiveClients ?? 0,
            uptime_seconds = stats != null ? (long)stats.Uptime.TotalSeconds : 0,
        };

        private static readonly DateTime _startTime = DateTime.UtcNow;
        private static string FormatUptime()
        {
            var u = DateTime.UtcNow - _startTime;
            return $"{(int)u.TotalHours:D2}:{u.Minutes:D2}:{u.Seconds:D2}";
        }

        private static string GenerateSecret(int len = 48)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*";
            var rng    = new Random(Environment.TickCount);
            var result = new char[len];
            for (int i = 0; i < len; i++) result[i] = chars[rng.Next(chars.Length)];
            return new string(result);
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _app?.DisposeAsync().GetAwaiter().GetResult();
            _cts?.Dispose();
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Request / Response DTOs
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Login request body.</summary>
    public sealed record LoginRequest(string Username, string Password);

    /// <summary>Body for POST /cameras/{id}/trigger-motion.</summary>
    public sealed class TriggerMotionRequest
    {
        public bool   Active          { get; set; }
        public double? DurationSeconds { get; set; }
    }

    /// <summary>Body for POST /onvif/event_service/api/TriggerEvent (DeskCamera-compatible).</summary>
    public sealed class OnvifTriggerRequest
    {
        public Guid CameraId { get; set; }
        public bool Active   { get; set; }
    }
}
