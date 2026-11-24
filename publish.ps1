# Installs UniDashboard (self-contained, unpackaged) to %LOCALAPPDATA%\UniDashboard
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'UniDashboard\bin'
Get-Process UniDashboard.App, UniDashboard.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($proj in 'UniDashboard.App', 'UniDashboard.Agent') {
    dotnet publish "$PSScriptRoot\$proj" -c Release -r win-x64 -p:Platform=x64 --self-contained -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}
Write-Host "Installed to $dest"
Write-Host "Start: $dest\UniDashboard.App.exe  (enable the background agent in Settings)"
