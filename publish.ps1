# Builds and installs UniDashboard (self-contained, unpackaged) for the current user into %LOCALAPPDATA%\UniDashboard\bin.
# Safe to re-run: it closes a running copy first, so it doubles as the update script.
$ErrorActionPreference = 'Stop'

# --- prerequisites
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and try again.'
}
$sdk = (& dotnet --version)
if (-not $sdk.StartsWith('10.')) { throw "The .NET 10 SDK is required (found $sdk). Install it from https://dotnet.microsoft.com/download/dotnet/10.0" }
$browser = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
             "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
             "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ }
if (-not $browser) { Write-Warning 'Neither Google Chrome nor Microsoft Edge was found; the university login needs one of them.' }

# --- build + install
$dest = Join-Path $env:LOCALAPPDATA 'UniDashboard\bin'
Get-Process UniDashboard.App, UniDashboard.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($proj in 'UniDashboard.App', 'UniDashboard.Agent') {
    dotnet publish "$PSScriptRoot\$proj" -c Release -r win-x64 -p:Platform=x64 --self-contained -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}
Write-Host "Installed to $dest"
Write-Host "Start: $dest\UniDashboard.App.exe  (enable the background agent in Settings > Sync)"
