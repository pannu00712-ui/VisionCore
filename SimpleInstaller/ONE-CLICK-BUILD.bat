@echo off
setlocal
title VisionCore - Build Setup EXE (One Click)
color 0B

echo =====================================================================
echo   VisionCore - One-Click Build
echo   Yeh script khud sab kar dega: publish + EXE banana.
echo   Aapko bas neeche dekhna hai.
echo =====================================================================
echo.

:: -- Check if .NET SDK is installed -------------------------------------
where dotnet >nul 2>&1
if %errorLevel% neq 0 (
    echo  .NET SDK installed nahi mila.
    echo.
    echo  Pehle yeh link kholen aur ".NET SDK x64" download/install karein:
    echo     https://dotnet.microsoft.com/en-us/download/dotnet/8.0
    echo.
    echo  Install karne ke baad yeh script dobara chalayen.
    echo.
    pause
    exit /b 1
)

echo  [OK] .NET SDK mil gaya.
echo.

:: -- Find the solution root (this script lives in SimpleInstaller, -----
::    solution root is one folder up) ---------------------------------
set ROOT=%~dp0..
set SERVICE_CSPROJ=%ROOT%\VisionCore.Service\VisionCore.Service.csproj
set OUT_DIR=%~dp0service

if not exist "%SERVICE_CSPROJ%" (
    echo  ERROR: VisionCore.Service.csproj nahi mila.
    echo  Expected: %SERVICE_CSPROJ%
    echo  Make sure yeh script SimpleInstaller folder ke andar hi hai,
    echo  aur uske bagal mein VisionCore.Service folder maujood hai.
    pause
    exit /b 1
)

echo  Step 1/2: VisionCore Service ko build kar rahe hain...
echo  (Pehli baar internet packages download honge, thora time lagega)
echo.

dotnet publish "%SERVICE_CSPROJ%" -c Release -r win-x64 --self-contained true -o "%OUT_DIR%"

if %errorLevel% neq 0 (
    echo.
    echo  ERROR: Build fail ho gaya. Upar wala error message dekhen.
    pause
    exit /b 1
)

echo.
echo  [OK] Build mukammal.
echo.
echo  Step 2/2: Setup EXE bana rahe hain...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Setup.ps1"

if %errorLevel% neq 0 (
    echo.
    echo  ERROR: EXE banane mein masla aaya. Upar wala message dekhen.
    pause
    exit /b 1
)

echo.
echo =====================================================================
echo   DONE! 
echo   "VisionCoreSetup.exe" is folder mein ban gaya hai:
echo      %~dp0
echo.
echo   Yehi file client ko bhej dein. Woh double-click karega,
echo   "Yes" dabayega, aur VisionCore khud install + auto-start ho jayega.
echo =====================================================================
echo.
pause
