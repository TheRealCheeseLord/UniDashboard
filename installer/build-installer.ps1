# Builds artifacts\UniDashboard-Setup-<version>.exe (+ .sha256) from the current sources.
#   .\installer\build-installer.ps1 -Version 0.2.0
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup). CI runs this on every release tag.
param([string]$Version = '0.1.0')
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts'
$publish = Join-Path $artifacts 'publish'
$name = "UniDashboard-Setup-$Version.exe"

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $publish | Out-Null

# App and agent share one folder, exactly like publish.ps1 does for the dev install.
foreach ($proj in 'UniDashboard.App', 'UniDashboard.Agent') {
    dotnet publish "$repo\$proj" -c Release -r win-x64 -p:Platform=x64 -p:Version=$Version --self-contained -o $publish
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

# Playwright ships Node runtimes for every OS (~570 MB uncompressed); only the Windows one is ever used.
Get-ChildItem "$publish\.playwright\node" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'win32_x64' } | Remove-Item -Recurse -Force

& $iscc /Qp "/DAppVersion=$Version" "/DPublishDir=$publish" "$PSScriptRoot\UniDashboard.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $artifacts $name
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
"$hash  $name" | Set-Content -Path "$setup.sha256" -Encoding ascii
Write-Host "Built $setup"
Write-Host "SHA-256 $hash"
