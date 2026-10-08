Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinEnum {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    public static List<IntPtr> VisibleForPid(uint want) {
        var outp = new List<IntPtr>();
        EnumWindows(delegate(IntPtr h, IntPtr lp) {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == want && IsWindowVisible(h)) outp.Add(h);
            return true;
        }, IntPtr.Zero);
        return outp;
    }
    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
}
"@

# Rules for this script, both learned the hard way:
#  1. pure ASCII only -- PowerShell 5.1 reads BOM-less UTF-8 as GBK;
#  2. every window operation is filtered by PROCESS ID of the instance WE started.
#     A looser "some window that isn't the main one" heuristic once closed an
#     unrelated application's window.
#  3. detect windows with Win32 EnumWindows, not UIA RootElement.Children -- UIA
#     does not surface owned WPF windows there, which produced a false negative.

$exe = $args[0]
$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $repoRoot 'tools\shots'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$p = Start-Process -FilePath $exe -PassThru
$thePid = [uint32]$p.Id

$main = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) { Write-Host 'FAIL: process exited early'; exit 1 }
    foreach ($h in [WinEnum]::VisibleForPid($thePid)) {
        $r = New-Object WinEnum+RECT
        [WinEnum]::GetWindowRect($h, [ref]$r) | Out-Null
        if (($r.Right - $r.Left) -gt 400) { $main = $h; break }
    }
    if ($main -ne [IntPtr]::Zero) { break }
}
if ($main -eq [IntPtr]::Zero) { Write-Host 'FAIL: no main window'; $p.Kill(); exit 1 }
Write-Host ('main hwnd=' + $main + ' title=[' + [WinEnum]::TitleOf($main) + ']')

$app = [System.Windows.Automation.AutomationElement]::FromHandle($main)
$menuCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::MenuItem)
$desktop = [System.Windows.Automation.AutomationElement]::RootElement

function Get-LogWin($thePid, $main) {
    foreach ($h in [WinEnum]::VisibleForPid($thePid)) {
        if ($h -eq $main) { continue }
        return $h
    }
    return [IntPtr]::Zero
}

function Open-LogWindow($app, $desktop, $menuCond) {
    $items = $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCond)
    $topNames = @()
    foreach ($it in $items) { $topNames += $it.Current.Name }
    $items[1].GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 700
    $all = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCond)
    foreach ($it in $all) {
        if ($topNames -notcontains $it.Current.Name) {
            $it.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            return $true
        }
    }
    return $false
}

Write-Host ('visible windows before: ' + [WinEnum]::VisibleForPid($thePid).Count)

if (-not (Open-LogWindow $app $desktop $menuCond)) { Write-Host 'FAIL: menu item not found'; $p.Kill(); exit 1 }
Start-Sleep -Milliseconds 1800

$logWin = Get-LogWin $thePid $main
if ($logWin -eq [IntPtr]::Zero) { Write-Host 'FAIL: log window did not appear'; $p.Kill(); exit 1 }

Write-Host ('OK: log window hwnd=' + $logWin + ' title=[' + [WinEnum]::TitleOf($logWin) + ']')

$r = New-Object WinEnum+RECT
[WinEnum]::GetWindowRect($logWin, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h2 = $r.Bottom - $r.Top
$bmp = New-Object Drawing.Bitmap $w, $h2
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $outDir 'commlog_window.png'), [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Host ('OK commlog_window.png ' + $w + 'x' + $h2)

# the log text, so it can be compared against the in-tab box
$logEl = [System.Windows.Automation.AutomationElement]::FromHandle($logWin)
$editCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit)
$edit = $logEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
if ($edit -ne $null) {
    $tp = $null
    if ($edit.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$tp)) {
        Write-Host '=== LogBox ==='
        Write-Host $tp.DocumentRange.GetText(-1)
        Write-Host '=== /LogBox ==='
    }
}

# reopening must reuse the window, not stack a second one
Open-LogWindow $app $desktop $menuCond | Out-Null
Start-Sleep -Milliseconds 1200
Write-Host ('visible windows after a second open: ' + [WinEnum]::VisibleForPid($thePid).Count + ' (expect 2: main + log)')

# closing OUR log window must not take the app down
[WinEnum]::SendMessage($logWin, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 1500
$p.Refresh()
if ($p.HasExited) { Write-Host 'FAIL: closing the log window killed the whole app' }
else { Write-Host 'OK: app survives closing the log window' }
Write-Host ('visible windows after closing the log: ' + [WinEnum]::VisibleForPid($thePid).Count + ' (expect 1)')

# reopen, so the next check has the log window open while the main window closes
Open-LogWindow $app $desktop $menuCond | Out-Null
Start-Sleep -Milliseconds 1500
Write-Host ('visible windows before closing main: ' + [WinEnum]::VisibleForPid($thePid).Count + ' (expect 2)')

[WinEnum]::SendMessage($main, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 400
    $p.Refresh()
    if ($p.HasExited) { break }
}
$p.Refresh()
if ($p.HasExited) {
    Write-Host ('OK: process exited with the log window still open (code=' + $p.ExitCode + ')')
} else {
    Write-Host 'FAIL: process still running after closing the main window'
    $p.Kill()
}
