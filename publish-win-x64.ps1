$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot "publish\osu-network-accel-win-x64"

Write-Host "Publishing to:"
Write-Host $outputDir
Write-Host

if (Test-Path $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}

Push-Location $projectRoot
try {
    cargo build --release --locked
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE" }
    New-Item -ItemType Directory -Force $outputDir | Out-Null
    Copy-Item (Join-Path $projectRoot "target\release\osu-network-accel.exe") `
        (Join-Path $outputDir "OsuNetworkAccel.exe")
}
finally {
    Pop-Location
}

Write-Host
Write-Host "Publish complete."
Write-Host "Output file:"
Write-Host (Join-Path $outputDir "OsuNetworkAccel.exe")
