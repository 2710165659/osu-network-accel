@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

set "APP_EXE=%~dp0OsuNetworkAccel.exe"
if not exist "%APP_EXE%" (
    echo OsuNetworkAccel.exe was not found in the same directory.
    echo Run this script from the published output folder.
    pause
    exit /b 1
)

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -WorkingDirectory '%~dp0' -Verb RunAs"
    exit /b
)

"%APP_EXE%" restore --app-root "%~dp0"
set "EXIT_CODE=%errorlevel%"

echo.
if "%EXIT_CODE%"=="0" (
    echo Network restored.
) else (
    echo Restore failed. Exit code: %EXIT_CODE%
)

pause
exit /b %EXIT_CODE%
