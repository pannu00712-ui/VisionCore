using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════════════
// IPC contract
// ═══════════════════════════════════════════════════════════════════════════════
//
// Both ends exchange newline-delimited JSON frames over a named pipe:
//   VisionCoreServicePipe  (defined in ServiceConstants.IpcPipeName)
//
// Message flow:
//   WPF tray  ──(request)──▶  Service  ──(response)──▶  WPF tray
//
// Supported commands (IpcCommand.Command field):
//   "GetStatus"      → IpcStatusResponse
//   "StartCamera"    → IpcAckResponse        (payload: { "cameraId": "<guid>" })
//   "StopCamera"     → IpcAckResponse        (payload: { "cameraId": "<guid>" })
//   "StartAll"       → IpcAckResponse
//   "StopAll"        → IpcAckResponse
//   "GetSettings"    → IpcSettingsResponse   (AppSettings JSON payload)
//   "Ping"           → IpcAckResponse        (health check)
//
// ═══════════════════════════════════════════════════════════════════════════════

namespace VisionCore.Ipc
{
    // ── Shared message types ──────────────────────────────────────────────────

    public sealed record IpcCommand(string Command, string? Payload = null);

    public sealed record IpcAckResponse(bool Success, string? Error = null);

    public sealed record IpcStatusResponse(
        bool   RtspOnline,
        bool   RestApiOnline,
        int    ActiveCameras,
        int    TotalClients,
        double TotalBitrateKbps,
        string Uptime);

    // ═══════════════════════════════════════════════════════════════════════════
    // IpcPipeServer  (runs inside VisionCore.Service / VisionCoreWorker)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Listens on <c>VisionCoreServicePipe</c> and dispatches incoming commands
    /// to the engine services.
    ///
    /// One connection at a time — the WPF tray app is the only expected client.
    /// After handling a message it loops back and waits for the next connection.
    /// </summary>
    public sealed class IpcPipeServer : IDisposable
    {
        private readonly ILogger<IpcPipeServer> _logger;
        private readonly Func<IpcCommand, Task<string>> _handler;

        private CancellationTokenSource? _cts;
        private Task?                    _listenTask;

        private static readonly JsonSerializerOptions JsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        public IpcPipeServer(
            ILogger<IpcPipeServer> logger,
            Func<IpcCommand, Task<string>> commandHandler)
        {
            _logger  = logger;
            _handler = commandHandler;
        }

        // ── Start / Stop ──────────────────────────────────────────────────────

        public void Start()
        {
            _cts        = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            _logger.LogInformation("IPC pipe server started on '{Pipe}'.",
                VisionCore.Service.ServiceConstants.IpcPipeName);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_listenTask != null)
                await _listenTask.ConfigureAwait(false);
        }

        // ── Listen loop ───────────────────────────────────────────────────────

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // NamedPipeServerStream is single-use: create a fresh one each iteration
                    using var pipe = new NamedPipeServerStream(
                        VisionCore.Service.ServiceConstants.IpcPipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(ct);

                    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
                        { AutoFlush = true };

                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    IpcCommand? cmd = null;
                    try
                    {
                        cmd = JsonSerializer.Deserialize<IpcCommand>(line, JsonOpts);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "IPC: malformed command JSON.");
                        var err = JsonSerializer.Serialize(new IpcAckResponse(false, "Invalid JSON"));
                        await writer.WriteLineAsync(err);
                        continue;
                    }

                    if (cmd == null) continue;

                    _logger.LogDebug("IPC ← {Command}", cmd.Command);

                    var responseJson = await _handler(cmd);
                    await writer.WriteLineAsync(responseJson);

                    _logger.LogDebug("IPC → {Response}", responseJson);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IPC pipe server iteration error.");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            _cts?.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // IpcPipeClient  (runs inside VisionCore.WPF tray app)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Connects to <c>VisionCoreServicePipe</c>, sends a command, and waits for
    /// a single response.
    ///
    /// Each call opens a fresh connection so there are no keep-alive concerns.
    /// Used by the WPF tray app when <see cref="AppSettings.RunAsService"/> = true
    /// to delegate engine commands to the Windows Service instead of the in-process
    /// engine.
    /// </summary>
    public sealed class IpcPipeClient
    {
        private readonly ILogger<IpcPipeClient> _logger;
        private readonly TimeSpan               _connectTimeout;

        private static readonly JsonSerializerOptions JsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        public IpcPipeClient(
            ILogger<IpcPipeClient> logger,
            TimeSpan? connectTimeout = null)
        {
            _logger         = logger;
            _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        }

        // ── SendAsync ─────────────────────────────────────────────────────────

        /// <summary>
        /// Send a command to the service and receive the raw JSON response string.
        /// </summary>
        public async Task<string> SendAsync(
            IpcCommand        command,
            CancellationToken ct = default)
        {
            using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName:   VisionCore.Service.ServiceConstants.IpcPipeName,
                direction:  PipeDirection.InOut,
                options:    PipeOptions.Asynchronous);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_connectTimeout);

            try
            {
                await pipe.ConnectAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Could not connect to '{VisionCore.Service.ServiceConstants.IpcPipeName}' " +
                    $"within {_connectTimeout.TotalSeconds:N0} s. " +
                    $"Is the VisionCore service running?");
            }

            using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
                { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

            var json = JsonSerializer.Serialize(command);
            await writer.WriteLineAsync(json.AsMemory(), ct);

            var response = await reader.ReadLineAsync(ct);
            return response ?? string.Empty;
        }

        // ── Typed helpers ─────────────────────────────────────────────────────

        /// <summary>Send a Ping command. Returns true if the service responds.</summary>
        public async Task<bool> PingAsync(CancellationToken ct = default)
        {
            try
            {
                var raw = await SendAsync(new IpcCommand("Ping"), ct);
                var ack = JsonSerializer.Deserialize<IpcAckResponse>(raw, JsonOpts);
                return ack?.Success == true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IPC Ping failed.");
                return false;
            }
        }

        /// <summary>Request live status from the service.</summary>
        public async Task<IpcStatusResponse?> GetStatusAsync(CancellationToken ct = default)
        {
            var raw = await SendAsync(new IpcCommand("GetStatus"), ct);
            return JsonSerializer.Deserialize<IpcStatusResponse>(raw, JsonOpts);
        }

        /// <summary>Ask the service to start a specific camera.</summary>
        public async Task<IpcAckResponse?> StartCameraAsync(Guid cameraId, CancellationToken ct = default)
        {
            var payload = JsonSerializer.Serialize(new { cameraId });
            var raw     = await SendAsync(new IpcCommand("StartCamera", payload), ct);
            return JsonSerializer.Deserialize<IpcAckResponse>(raw, JsonOpts);
        }

        /// <summary>Ask the service to stop a specific camera.</summary>
        public async Task<IpcAckResponse?> StopCameraAsync(Guid cameraId, CancellationToken ct = default)
        {
            var payload = JsonSerializer.Serialize(new { cameraId });
            var raw     = await SendAsync(new IpcCommand("StopCamera", payload), ct);
            return JsonSerializer.Deserialize<IpcAckResponse>(raw, JsonOpts);
        }

        /// <summary>Ask the service to start all enabled cameras.</summary>
        public async Task<IpcAckResponse?> StartAllAsync(CancellationToken ct = default)
        {
            var raw = await SendAsync(new IpcCommand("StartAll"), ct);
            return JsonSerializer.Deserialize<IpcAckResponse>(raw, JsonOpts);
        }

        /// <summary>Ask the service to stop all cameras.</summary>
        public async Task<IpcAckResponse?> StopAllAsync(CancellationToken ct = default)
        {
            var raw = await SendAsync(new IpcCommand("StopAll"), ct);
            return JsonSerializer.Deserialize<IpcAckResponse>(raw, JsonOpts);
        }
    }
}
