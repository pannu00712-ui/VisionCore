@echo off
title VisionCore Uninstall

set SERVICE_NAME=VisionCoreService
set INSTALL_DIR=%ProgramFiles%\VisionCore

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo  This needs to run as Administrator.
    pause
    exit /b 1
)

echo  Stopping and removing VisionCore service...
sc stop "%SERVICE_NAME%" >nul 2>&1
timeout /t 3 /nobreak >nul
sc delete "%SERVICE_NAME%" >nul 2>&1

echo  Removing files...
rmdir /S /Q "%INSTALL_DIR%" >nul 2>&1

echo  VisionCore has been removed.
pause
