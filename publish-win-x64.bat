@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo dotnet SDK not found. Install .NET SDK first.
    pause
    exit /b 1
)

set "OUTPUT_DIR=%~dp0publish\osu-network-accel-win-x64"
set "STAGING_DIR=%~dp0publish\_staging\win-x64"

echo Publishing to:
echo %OUTPUT_DIR%
echo.

if exist "%STAGING_DIR%" rmdir /s /q "%STAGING_DIR%"
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%STAGING_DIR%" >nul 2>&1
mkdir "%OUTPUT_DIR%" >nul 2>&1

dotnet publish ".\OsuNetworkAccel.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  /p:PublishSingleFile=true ^
  /p:EnableCompressionInSingleFile=true ^
  /p:IncludeNativeLibrariesForSelfExtract=true ^
  /p:DebugType=None ^
  /p:DebugSymbols=false ^
  -o "%STAGING_DIR%"

set "EXIT_CODE=%errorlevel%"
if not "%EXIT_CODE%"=="0" goto finish

copy /y "%STAGING_DIR%\OsuNetworkAccel.exe" "%OUTPUT_DIR%\OsuNetworkAccel.exe" >nul
copy /y "%STAGING_DIR%\accelerate-osu-network.bat" "%OUTPUT_DIR%\accelerate-osu-network.bat" >nul
copy /y "%STAGING_DIR%\restore-osu-network.bat" "%OUTPUT_DIR%\restore-osu-network.bat" >nul

if not exist "%OUTPUT_DIR%\OsuNetworkAccel.exe" set "EXIT_CODE=1"
if not exist "%OUTPUT_DIR%\accelerate-osu-network.bat" set "EXIT_CODE=1"
if not exist "%OUTPUT_DIR%\restore-osu-network.bat" set "EXIT_CODE=1"

:finish
echo.

if "%EXIT_CODE%"=="0" (
    echo Publish complete.
    echo Output directory: %OUTPUT_DIR%
    echo Final files: OsuNetworkAccel.exe, accelerate-osu-network.bat, restore-osu-network.bat
) else (
    echo Publish failed. Exit code: %EXIT_CODE%
)

pause
exit /b %EXIT_CODE%
