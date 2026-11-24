# Builds and installs UniDashboard (self-contained, unpackaged) for the current user into %LOCALAPPDATA%\UniDashboard\bin.
# Safe to re-run: it closes a running copy first, so it doubles as the update script.
$ErrorActionPreference = 'Stop'

# --- prerequisites
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and try again.'
}
$sdk = (& dotnet --version)
if (-not $sdk.StartsWith('10.')) { throw "The .NET 10 SDK is required (found $sdk). Install it from https://dotnet.microsoft.com/download/dotnet/10.0" }
# The sign-in window (only needed for a manual login) uses the Microsoft Edge WebView2 Runtime; it ships with Windows 11 and Microsoft Edge.
$wv2 = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
if (-not ($wv2 | Where-Object { (Get-ItemProperty $_ -ErrorAction SilentlyContinue).pv })) {
    Write-Warning 'The Microsoft Edge WebView2 Runtime was not found; the manual sign-in window needs it (https://go.microsoft.com/fwlink/p/?LinkId=2124703).'
}

# --- build + install
$dest = Join-Path $env:LOCALAPPDATA 'UniDashboard\bin'
Get-Process UniDashboard.App, UniDashboard.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($proj in 'UniDashboard.App', 'UniDashboard.Agent') {
    dotnet publish "$PSScriptRoot\$proj" -c Release -r win-x64 -p:Platform=x64 --self-contained -o $dest
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}
# Leftovers of the old browser driver (publishing does not delete files that are no longer part of the build).
Remove-Item "$dest\.playwright", "$dest\Microsoft.Playwright.dll", "$dest\playwright.ps1" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Installed to $dest"
Write-Host "Start: $dest\UniDashboard.App.exe  (enable the background agent in Settings > Sync)"
