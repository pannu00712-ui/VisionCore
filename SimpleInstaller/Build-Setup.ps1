<#
  Build-Setup.ps1
  ===============================================================================
  Builds VisionCoreSetup.exe - a single self-extracting installer EXE that the
  client double-clicks. No WiX, no MSI, no third-party tools required.

  Run this ONCE on a Windows build machine (with .NET SDK installed) after you
  have published the Service project:

      dotnet publish ..\VisionCore.Service\VisionCore.Service.csproj `
          -c Release -r win-x64 --self-contained true -o .\service

      .\Build-Setup.ps1

  Output: VisionCoreSetup.exe  (in this folder)
  Hand THIS ONE FILE to the client. They double-click it, click "Yes" on the
  UAC prompt, and VisionCore installs + starts automatically. Nothing else
  is ever visible.
  ===============================================================================
#>

$ErrorActionPreference = "Stop"
$root      = $PSScriptRoot
$servicDir = Join-Path $root "service"
$installBat = Join-Path $root "Install.bat"
$outExe    = Join-Path $root "VisionCoreSetup.exe"
$payloadZip = Join-Path $root "payload.zip"

if (-not (Test-Path $servicDir)) {
    Write-Host "ERROR: '$servicDir' not found." -ForegroundColor Red
    Write-Host "Publish the Service project into a 'service' folder here first:" -ForegroundColor Yellow
    Write-Host "  dotnet publish ..\VisionCore.Service\VisionCore.Service.csproj -c Release -r win-x64 --self-contained true -o .\service" -ForegroundColor Yellow
    exit 1
}

Write-Host "Packaging service files + installer script..." -ForegroundColor Cyan

# 1. Zip the service folder + Install.bat together
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
$tempStage = Join-Path $env:TEMP "visioncore_stage_$(Get-Random)"
$tempStageService = Join-Path $tempStage "service"
New-Item -ItemType Directory -Path $tempStage -Force | Out-Null
# Create the destination "service" folder FIRST, then copy the *contents* of
# $servicDir into it (trailing \* on the source). Copying the folder itself
# with -Recurse when the destination doesn't pre-exist can nest it one level
# too deep (tempStage\service\service\...) on some PowerShell versions -
# pre-creating the destination avoids that ambiguity entirely.
New-Item -ItemType Directory -Path $tempStageService -Force | Out-Null
Copy-Item (Join-Path $servicDir "*") $tempStageService -Recurse -Force
Copy-Item $installBat (Join-Path $tempStage "Install.bat") -Force

# Sanity check: make sure the staged service folder actually has files in it
# before we zip it up - fail loudly here instead of producing a broken EXE.
$stagedFiles = Get-ChildItem $tempStageService -Recurse -File -ErrorAction SilentlyContinue
if (-not $stagedFiles -or $stagedFiles.Count -eq 0) {
    Write-Host "ERROR: staged 'service' folder is empty after copy - aborting." -ForegroundColor Red
    Write-Host "Source was: $servicDir" -ForegroundColor Yellow
    Remove-Item $tempStage -Recurse -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "Staged $($stagedFiles.Count) service file(s)." -ForegroundColor DarkGray

Compress-Archive -Path "$tempStage\*" -DestinationPath $payloadZip -CompressionLevel Optimal
Remove-Item $tempStage -Recurse -Force

# 2. Build a small self-extracting stub EXE.
#    The stub is a PowerShell script compiled with Add-Type into a tiny .NET
#    console app that: extracts payload.zip (embedded as a resource) to a temp
#    folder, then runs Install.bat elevated, then cleans up.
$stubSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

class SetupStub
{
    static int Main()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "VisionCoreSetup_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Extract embedded payload.zip resource
            var asm = Assembly.GetExecutingAssembly();
            string resourceName = "payload.zip";
            string zipPath = Path.Combine(tempDir, resourceName);

            using (var stream = asm.GetManifestResourceStream("SetupStub.payload.zip"))
            {
                if (stream == null)
                {
                    Console.WriteLine("ERROR: embedded payload not found.");
                    return 1;
                }
                using (var fs = File.Create(zipPath))
                    stream.CopyTo(fs);
            }

            ZipFile.ExtractToDirectory(zipPath, tempDir);

            string installBat = Path.Combine(tempDir, "Install.bat");

            var psi = new ProcessStartInfo
            {
                FileName = installBat,
                WorkingDirectory = tempDir,
                UseShellExecute = true,
                Verb = "runas"   // forces the UAC elevation prompt
            };

            var proc = Process.Start(psi);
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Setup failed: " + ex.Message);
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
'@

$stubCs = Join-Path $root "SetupStub.cs"
Set-Content -Path $stubCs -Value $stubSource -Encoding UTF8

Write-Host "Compiling self-extracting EXE..." -ForegroundColor Cyan

$cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $cscPath)) {
    $cscPath = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $cscPath)) {
    Write-Host "ERROR: csc.exe (.NET Framework compiler) not found. Install .NET Framework, or use 'dotnet build' fallback below." -ForegroundColor Red
    exit 1
}

& $cscPath /nologo /target:exe /platform:x64 `
    /out:$outExe `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /resource:"$payloadZip,SetupStub.payload.zip" `
    $stubCs

if ($LASTEXITCODE -ne 0) {
    Write-Host "Compilation failed." -ForegroundColor Red
    exit 1
}

Remove-Item $stubCs -Force
Remove-Item $payloadZip -Force

Write-Host ""
Write-Host "Done! -> $outExe" -ForegroundColor Green
Write-Host "Give this single file to the client. They double-click it," -ForegroundColor Green
Write-Host "approve the admin prompt, and VisionCore installs + auto-starts." -ForegroundColor Green
