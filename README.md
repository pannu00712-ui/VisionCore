# VisionCore

Windows camera streaming platform — screen capture, webcam, RTSP/ONVIF via MediaMTX.

## Prerequisites

- .NET 8 SDK
- Visual Studio 2022 17.8+ (or `dotnet build`)
- Windows 10 x64 or later

## Open in Visual Studio

```
File → Open → Solution → VisionCore.sln
```

Set startup project to **VisionCore.WPF** for the desktop app, or  
**VisionCore.Service** to run as a headless service.

## First build

```powershell
dotnet restore
dotnet build -c Debug
dotnet run --project VisionCore.WPF
```

## Before first run

1. MediaMTX downloads automatically on first launch.  
2. Replace `UpdateVerifier.EmbeddedPublicKeyBase64` in `UpdateVerifier.cs`  
   with your real ED25519 public key (or updates will always fail verification).

## Project layout

| Project | Purpose |
|---|---|
| `VisionCore.Core` | Shared engine: RTSP, FFmpeg, motion detection, REST API, update system |
| `VisionCore.Service` | Windows Service host (`VisionCoreService`) |
| `VisionCore.WPF` | WPF tray application |
| `VisionCore.Installer` | WiX 4 MSI + Burn bootstrapper |

## Known stubs (not yet implemented)

- `OnvifServer` — registers and starts but does not implement SOAP/WS-Discovery
- `RtspHealthMonitor.IsPublisherAlive()` — always returns true; wire to `RtspStreamManager._publishers`
- IPC Named Pipe server — `IpcPipeServer` is implemented; wire it into `VisionCoreWorker.InitialiseAsync()`

See `VisionCore_Technical_Documentation.docx` for full architecture notes.
