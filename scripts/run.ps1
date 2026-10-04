# Builds and starts SWISH from source (hand gestures + voice in one app, lives in the tray).
#   .\scripts\run.ps1             the app
#   .\scripts\run.ps1 -Console    the two original console programs instead, side by side
param([switch]$Console)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = "$root\src"

dotnet build "$root\SWISH.slnx" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $Console) {
    # The app reads ELEVENLABS_API_KEY from your user environment itself, so a fresh setx works without a new terminal.
    Start-Process "$src\SWISH.App\bin\Release\net10.0-windows\SWISH.exe"
    Write-Host "SWISH started. It lives in the system tray; double-click the icon to show the window."
    return
}

if (-not $env:ELEVENLABS_API_KEY) {
    $env:ELEVENLABS_API_KEY = [Environment]::GetEnvironmentVariable('ELEVENLABS_API_KEY', 'User')
}
# The gesture console app loads model/ and calibration.json relative to its own folder.
Start-Process dotnet -WorkingDirectory "$src\HandGestureRecognition" `
    -ArgumentList "run --no-build -c Release --project `"$src\HandGestureRecognition`""
dotnet run --no-build -c Release --project "$src\VoiceKeys"
