# Regenerates docs/screenshots/*.png from FAKE demo data (never touches your real data).
#   1. seeds a throwaway data folder via the Smoke tool (--seed-demo)
#   2. starts the installed app against it (UNIDASHBOARD_DATA_DIR) and navigates with the app's command-line switches
#   3. grabs the window of each page
# Run `.\publish.ps1` first so the installed app is current. The window is resized to 1440x900 for consistent shots.
param(
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'UniDashboard\bin\UniDashboard.App.exe'),
    [string]$Out = (Join-Path $PSScriptRoot 'screenshots')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$data = Join-Path $env:TEMP 'uni-demo'
if (Get-Process UniDashboard.App -ErrorAction SilentlyContinue) { throw 'Close UniDashboard first (single-instance app).' }

dotnet run --project "$repo\UniDashboard.Smoke" -- --seed-demo $data | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'seeding failed' }
New-Item -ItemType Directory -Force $Out | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint flags);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
}
'@
[void][Win]::SetProcessDPIAware()

$env:UNIDASHBOARD_DATA_DIR = $data
$proc = Start-Process $Exe -PassThru
try {
    $deadline = (Get-Date).AddSeconds(40)
    while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $proc.Refresh() }
    $h = $proc.MainWindowHandle
    if ($h -eq 0) { throw 'app window did not appear' }
    [void][Win]::ShowWindow($h, 9); [void][Win]::MoveWindow($h, 40, 40, 1440, 900, $true)
    # Keep the app above everything else: the screenshot is a screen grab, so nothing else may overlap it.
    [void][Win]::SetWindowPos($h, [IntPtr]::new(-1), 40, 40, 1440, 900, 0x0040)   # HWND_TOPMOST, SWP_SHOWWINDOW
    [void][Win]::SetForegroundWindow($h)
    Start-Sleep -Seconds 3

    function Shot([string]$name, [string]$switches, [int]$wait = 2) {
        if ($switches) { Start-Process $Exe -ArgumentList $switches -Wait }   # redirected to the running instance
        Start-Sleep -Seconds $wait
        $rr = New-Object Win+RECT; [void][Win]::DwmGetWindowAttribute($h, 9, [ref]$rr, [Runtime.InteropServices.Marshal]::SizeOf($rr))
        # A click on the empty title bar moves keyboard focus off the hamburger button, whose focus tooltip would otherwise show.
        [Windows.Forms.Cursor]::Position = New-Object Drawing.Point ($rr.L + 700), ($rr.T + 14)
        [Win]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [Win]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300
        # Park the mouse outside the window so no hover tooltip ("Close Navigation") ends up in the picture.
        [Windows.Forms.Cursor]::Position = New-Object Drawing.Point ([Windows.Forms.Screen]::PrimaryScreen.Bounds.Width - 2), ([Windows.Forms.Screen]::PrimaryScreen.Bounds.Height - 2)
        Start-Sleep -Milliseconds 700
        [void][Win]::SetForegroundWindow($h)
        $r = New-Object Win+RECT
        [void][Win]::DwmGetWindowAttribute($h, 9, [ref]$r, [Runtime.InteropServices.Marshal]::SizeOf($r))   # DWMWA_EXTENDED_FRAME_BOUNDS
        $w = $r.R - $r.L; $ht = $r.B - $r.T
        $bmp = New-Object Drawing.Bitmap $w, $ht
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
        $g.Dispose(); $bmp.Save((Join-Path $Out "$name.png"), [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        Write-Host "saved $name.png"
    }
    function ClickByName([string]$name) {
        $root = [Windows.Automation.AutomationElement]::FromHandle($h)
        $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $name
        $el = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -eq $el) { Write-Warning "no element named '$name'"; return }
        $p = $null
        if ($el.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand() }
        elseif ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() }
        elseif ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select() }
    }

    Shot 'assignments'      ''
    Shot 'assignment-detail' '--assignment=2'
    ClickByName 'History'; Start-Sleep -Seconds 1; Shot 'assignment-history' '' 1
    Shot 'calendar-week'    '--calendar=week'
    # Switching to the page that is already open does not change its view, so go via another page first.
    Start-Process $Exe -ArgumentList '--planner' -Wait; Start-Sleep -Seconds 1
    Shot 'calendar-month'   '--calendar=month'
    Shot 'courses'          '--course=100004-1-2026W'
    Shot 'exam-planner'     '--planner'
    Shot 'export-ics'       '--export=ics'
    ClickByName 'Sync to Google Calendar'; Shot 'export-google' '' 1
    # NOT captured: Settings > Accounts. It shows the u:account username from the Windows Credential Manager
    # (real, not part of the demo data folder). The other settings tabs are safe:
    Start-Process $Exe -ArgumentList '--settings=sync' -Wait; Start-Sleep -Seconds 1
    Shot 'settings-sync' '' 1
    ClickByName 'Notifications'; Shot 'settings-notifications' '' 1
    ClickByName 'About & updates'; Shot 'settings-about' '' 1
}
finally { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Remove-Item Env:\UNIDASHBOARD_DATA_DIR }
