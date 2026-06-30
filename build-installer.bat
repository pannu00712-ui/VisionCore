@echo off
setlocal EnableDelayedExpansion
title VisionCore Installer Builder
color 0A

echo.
echo  ================================================================
echo    VisionCore Installer Builder
echo    Produces: VisionCore-1.0.0-Setup-x64.exe
echo              VisionCore-1.0.0-Setup-x86.exe
echo  ================================================================
echo.

:: ── Step 0: Must be run from solution root ───────────────────────────────────
if not exist "VisionCore.sln" (
    echo  [ERROR] Run this script from the VisionCore solution root folder.
    pause & exit /b 1
)

set "ROOT=%CD%"
set "VERSION=1.0.0"

:: ── Step 1: .NET SDK ─────────────────────────────────────────────────────────
echo  [1/8] Checking .NET SDK...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  [ERROR] dotnet not found. Install .NET 8 SDK:
    echo          https://dotnet.microsoft.com/download/dotnet/8.0
    pause & exit /b 1
)
for /f "tokens=*" %%v in ('dotnet --version') do echo         Version: %%v

:: ── Step 2: WiX tool ─────────────────────────────────────────────────────────
echo  [2/8] Checking WiX toolset...
wix --version >nul 2>&1
if errorlevel 1 (
    echo         Not found — installing WiX...
    dotnet tool install --global wix
    if errorlevel 1 (
        echo  [ERROR] WiX install failed.
        pause & exit /b 1
    )
    set "PATH=%PATH%;%USERPROFILE%\.dotnet\tools"
)
for /f "tokens=*" %%v in ('wix --version 2^>nul') do echo         WiX %%v

:: ── Step 3: WiX extensions ───────────────────────────────────────────────────
echo  [3/8] Adding WiX extensions...
wix extension add WixToolset.UI.wixext       --global >nul 2>&1
wix extension add WixToolset.Util.wixext     --global >nul 2>&1
wix extension add WixToolset.Firewall.wixext --global >nul 2>&1
wix extension add WixToolset.Bal.wixext      --global >nul 2>&1
echo         OK

:: ── Step 4: Publish x64 ──────────────────────────────────────────────────────
echo  [4/8] Publishing x64 (Release)...

echo         WPF x64...
dotnet publish "%ROOT%\VisionCore.WPF\VisionCore.WPF.csproj" ^
    -c Release -r win-x64 --self-contained false ^
    -o "%ROOT%\publish\x64\app" ^
    /p:DebugType=none /p:DebugSymbols=false /p:PublishSingleFile=false >nul
if errorlevel 1 ( echo  [ERROR] WPF x64 publish failed. & pause & exit /b 1 )

echo         Service x64...
dotnet publish "%ROOT%\VisionCore.Service\VisionCore.Service.csproj" ^
    -c Release -r win-x64 --self-contained false ^
    -o "%ROOT%\publish\x64\service" ^
    /p:DebugType=none /p:DebugSymbols=false /p:PublishSingleFile=false >nul
if errorlevel 1 ( echo  [ERROR] Service x64 publish failed. & pause & exit /b 1 )
echo         OK

:: ── Step 5: Publish x86 ──────────────────────────────────────────────────────
echo  [5/8] Publishing x86 (Release)...

echo         WPF x86...
dotnet publish "%ROOT%\VisionCore.WPF\VisionCore.WPF.csproj" ^
    -c Release -r win-x86 --self-contained false ^
    -o "%ROOT%\publish\x86\app" ^
    /p:DebugType=none /p:DebugSymbols=false /p:PublishSingleFile=false >nul
if errorlevel 1 ( echo  [ERROR] WPF x86 publish failed. & pause & exit /b 1 )

echo         Service x86...
dotnet publish "%ROOT%\VisionCore.Service\VisionCore.Service.csproj" ^
    -c Release -r win-x86 --self-contained false ^
    -o "%ROOT%\publish\x86\service" ^
    /p:DebugType=none /p:DebugSymbols=false /p:PublishSingleFile=false >nul
if errorlevel 1 ( echo  [ERROR] Service x86 publish failed. & pause & exit /b 1 )
echo         OK

:: ── Step 6: Build x64 MSI + Bundle ───────────────────────────────────────────
echo  [6/8] Building x64 installer (MSI)...
dotnet build "%ROOT%\VisionCore.Installer\VisionCore.Installer.csproj" ^
    -c Release ^
    /p:SourceDir="%ROOT%\publish\x64" ^
    /p:OutputName=VisionCore-%VERSION%-Setup-x64 ^
    /p:ProductVersion=%VERSION% ^
    /p:InstallerPlatform=x64 ^
    /p:IntermediateOutputPath=obj\x64\ ^
    /p:OutputPath=bin\Release\x64\
if errorlevel 1 ( echo  [ERROR] x64 MSI build failed. & pause & exit /b 1 )
echo         OK

echo  [7/8] Building x64 installer (Bundle .exe)...
dotnet build "%ROOT%\VisionCore.Installer\VisionCore.Bundle.csproj" ^
    -c Release ^
    /p:MsiPath="%ROOT%\VisionCore.Installer\bin\Release\x64\VisionCore-%VERSION%-Setup-x64.msi" ^
    /p:OutputName=VisionCore-%VERSION%-Setup-x64 ^
    /p:ProductVersion=%VERSION% ^
    /p:InstallerPlatform=x64 ^
    /p:IntermediateOutputPath=obj\x64-bundle\ ^
    /p:OutputPath=bin\Release\x64\
if errorlevel 1 ( echo  [ERROR] x64 Bundle build failed. & pause & exit /b 1 )
echo         OK

:: ── Step 8: Build x86 MSI + Bundle ───────────────────────────────────────────
echo  [8/8] Building x86 installer (MSI + Bundle .exe)...
dotnet build "%ROOT%\VisionCore.Installer\VisionCore.Installer.csproj" ^
    -c Release ^
    /p:SourceDir="%ROOT%\publish\x86" ^
    /p:OutputName=VisionCore-%VERSION%-Setup-x86 ^
    /p:ProductVersion=%VERSION% ^
    /p:InstallerPlatform=x86 ^
    /p:IntermediateOutputPath=obj\x86\ ^
    /p:OutputPath=bin\Release\x86\
if errorlevel 1 ( echo  [ERROR] x86 MSI build failed. & pause & exit /b 1 )

dotnet build "%ROOT%\VisionCore.Installer\VisionCore.Bundle.csproj" ^
    -c Release ^
    /p:MsiPath="%ROOT%\VisionCore.Installer\bin\Release\x86\VisionCore-%VERSION%-Setup-x86.msi" ^
    /p:OutputName=VisionCore-%VERSION%-Setup-x86 ^
    /p:ProductVersion=%VERSION% ^
    /p:InstallerPlatform=x86 ^
    /p:IntermediateOutputPath=obj\x86-bundle\ ^
    /p:OutputPath=bin\Release\x86\
if errorlevel 1 ( echo  [ERROR] x86 Bundle build failed. & pause & exit /b 1 )
echo         OK

:: ── Done — show output ────────────────────────────────────────────────────────
echo.
echo  ================================================================
echo    BUILD COMPLETE  ^(VisionCore %VERSION%^)
echo  ================================================================
echo.

set "OUTDIR=%ROOT%\VisionCore.Installer\bin\Release"
set "X64EXE=" & set "X86EXE=" & set "X64MSI=" & set "X86MSI="

for /r "%OUTDIR%" %%f in (*x64*.exe) do set "X64EXE=%%f"
for /r "%OUTDIR%" %%f in (*x86*.exe) do set "X86EXE=%%f"
for /r "%OUTDIR%" %%f in (*x64*.msi) do set "X64MSI=%%f"
for /r "%OUTDIR%" %%f in (*x86*.msi) do set "X86MSI=%%f"

if defined X64EXE echo    x64 Setup:  %X64EXE%
if defined X86EXE echo    x86 Setup:  %X86EXE%
echo.
if defined X64MSI echo    x64 MSI:    %X64MSI%
if defined X86MSI echo    x86 MSI:    %X86MSI%
echo.
echo    Share .exe files with users.
echo    .msi files are for enterprise/GPO deployment.
echo  ================================================================
echo.

:: Open output folder
if defined X64EXE (
    for %%f in ("%X64EXE%") do explorer "%%~dpf"
)

pause
