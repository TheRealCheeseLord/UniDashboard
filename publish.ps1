# Installs MoodleDashboard (self-contained, unpackaged) to %LOCALAPPDATA%\MoodleDashboard
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'MoodleDashboard\bin'
Get-Process MoodleDashboard.App, MoodleDashboard.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($proj in 'MoodleDashboard.App', 'MoodleDashboard.Agent') {
    dotnet publish "$PSScriptRoot\$proj" -c Release -r win-x64 -p:Platform=x64 --self-contained -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}
Write-Host "Installed to $dest"
Write-Host "Start: $dest\MoodleDashboard.App.exe  (enable the background agent in Settings)"
