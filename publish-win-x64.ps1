$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot "publish\osu-network-accel-win-x64"

Write-Host "Publishing to:"
Write-Host $outputDir
Write-Host

if (Test-Path $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}

dotnet publish (Join-Path $projectRoot "OsuNetworkAccel.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:EnableCompressionInSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    -o $outputDir

Write-Host
Write-Host "Publish complete."
Write-Host "Output file:"
Write-Host (Join-Path $outputDir "OsuNetworkAccel.exe")
