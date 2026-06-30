@echo off
setlocal enabledelayedexpansion
title VisionCore Setup

:: =============================================================================
::  VisionCore - Simple Installer
::  -----------------------------------------------------------------------
::  What this does:
::    1. Copies the published Service files to Program Files.
::    2. Registers VisionCore as a Windows Service.
::    3. Sets the service to start AUTOMATICALLY every time Windows boots.
::    4. Starts the service immediately (no reboot needed).
::
::  Client never sees a window, tray icon, or settings screen.
::  This is the ONLY thing the client needs to double-click.
:: =============================================================================

set SERVICE_NAME=VisionCoreService
set DISPLAY_NAME=VisionCore Streaming Engine
set INSTALL_DIR=%ProgramFiles%\VisionCore
set EXE_NAME=VisionCore.Service.exe

:: -- Require Administrator ------------------------------------------------
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo.
    echo  This installer needs to run as Administrator.
    echo  Right-click Install.bat and choose "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo.
echo  Installing VisionCore...
echo.

:: -- 1. Copy files --------------------------------------------------------
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"

:: %~dp0 = folder this .bat is running from.
:: Expects the published Service output to sit in a sibling "service" folder,
:: e.g.   SimpleInstaller\Install.bat
::        SimpleInstaller\service\VisionCore.Service.exe  (+ all DLLs)
if not exist "%~dp0service\%EXE_NAME%" (
    echo  ERROR: "%~dp0service\%EXE_NAME%" not found.
    echo  The "service" folder next to Install.bat is missing or empty.
    echo  This usually means the packaging step did not complete successfully.
    pause
    exit /b 1
)

xcopy "%~dp0service\*" "%INSTALL_DIR%\" /E /I /Y /Q >nul
if %errorLevel% neq 0 (
    echo  ERROR: Could not copy files. Check that the "service" folder exists
    echo  next to Install.bat and contains the published Service build.
    pause
    exit /b 1
)

:: -- 2. Stop + remove any previous install (safe upgrade) -------------------
sc query "%SERVICE_NAME%" >nul 2>&1
if %errorLevel% equ 0 (
    echo  Existing installation found - upgrading...
    sc stop "%SERVICE_NAME%" >nul 2>&1
    timeout /t 3 /nobreak >nul
    sc delete "%SERVICE_NAME%" >nul 2>&1
    timeout /t 2 /nobreak >nul
)

:: -- 3. Register the Windows Service -------------------------------------
sc create "%SERVICE_NAME%" ^
    binPath= "\"%INSTALL_DIR%\%EXE_NAME%\"" ^
    DisplayName= "%DISPLAY_NAME%" ^
    start= auto

if %errorLevel% neq 0 (
    echo  ERROR: Failed to create the Windows Service.
    pause
    exit /b 1
)

:: Friendly description shown in services.msc
sc description "%SERVICE_NAME%" "Manages RTSP/ONVIF camera streams, motion detection, and the REST API for the VisionCore surveillance platform. Starts automatically with Windows." >nul

:: Restart automatically if it ever crashes (1 min delay, then 1 min, then 1 min)
sc failure "%SERVICE_NAME%" reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul

:: -- 4. Start it now ------------------------------------------------------
sc start "%SERVICE_NAME%" >nul 2>&1

echo.
echo  =====================================================
echo   VisionCore installed successfully.
echo   It is now running silently in the background and
echo   will start automatically every time this PC boots.
echo  =====================================================
echo.
timeout /t 4 >nul
exit /b 0
