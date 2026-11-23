# Installs MoodleLite (self-contained, unpackaged) to %LOCALAPPDATA%\MoodleLite
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'MoodleLite\bin'
Get-Process MoodleLite.App, MoodleLite.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($proj in 'MoodleLite.App', 'MoodleLite.Agent') {
    dotnet publish "$PSScriptRoot\$proj" -c Release -r win-x64 -p:Platform=x64 --self-contained -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}
Write-Host "Installed to $dest"
Write-Host "Start: $dest\MoodleLite.App.exe  (enable the background agent in Settings)"
