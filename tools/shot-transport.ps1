Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinApi2 {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
"@

# This script does exactly two things: take screenshots, and use UI Automation to switch
# the "connection method" combo box to Telnet, then judge by control visibility whether
# the switch took effect.
#
# HARD RULES (both learned the hard way):
#  1) NEVER use global mouse/keyboard (SetCursorPos / mouse_event / SendKeys). A previous
#     version did, focus got stolen by another window, and the script captured the user's
#     browser instead of the app.
#  2) ALWAYS filter by process id. A desktop-wide element search pulls in other
#     applications' controls (measured: 67 of them on this machine).
# Screenshots must not be compared byte-by-byte (the caret blinks), so the pass/fail
# criterion here is control visibility, not the image.
# Keep this file PURE ASCII: PowerShell 5.1 reads BOM-less UTF-8 as GBK.

$repoRoot = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repoRoot 'bin\Release\PoECommandTool.exe'
$outDir = Join-Path $repoRoot 'tools\shots'
$telnetLabel = 'Telnet'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Save-Window([IntPtr]$handle, [string]$name) {
    $r = New-Object WinApi2+RECT
    [WinApi2]::GetWindowRect($handle, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { Write-Host 'FAIL: window size is 0'; return }
    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $outDir $name), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Host ('OK: ' + $name + '  ' + $w + 'x' + $h)
}

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5

# Poll for the real main window: the first handle can be a tiny placeholder.
$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 30; $i++) {
    $p.Refresh()
    if ($p.HasExited) { Write-Host ('FAIL: exited code=' + $p.ExitCode); exit 1 }
    $h = $p.MainWindowHandle
    if ($h -ne [IntPtr]::Zero) {
        $r = New-Object WinApi2+RECT
        [WinApi2]::GetWindowRect($h, [ref]$r) | Out-Null
        if (($r.Right - $r.Left) -gt 300) { $handle = $h; break }
    }
    Start-Sleep -Milliseconds 500
}
if ($handle -eq [IntPtr]::Zero) { Write-Host 'FAIL: no main window handle'; $p.Kill(); exit 1 }

[WinApi2]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 800

$app = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

function Find-ById([string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Is-Visible($element) {
    return $element -ne $null -and -not $element.Current.IsOffscreen
}

# Sanity check: a control that certainly exists. If even this is missing, the probe is
# broken -- do not conclude anything about the UI from it.
$probe = Find-ById 'PortCombo'
Write-Host ('PortCombo found: ' + ($probe -ne $null))
if ($probe -eq $null) { Write-Host 'FAIL: probe broken (known control not found)'; $p.Kill(); exit 1 }

Save-Window $handle 'transport-serial.png'
$serialHostVisible = Is-Visible (Find-ById 'HostBox')
Write-Host ('serial mode -> HostBox visible: ' + $serialHostVisible + '  PortCombo visible: ' + (Is-Visible $probe))

$combo = Find-ById 'TransportCombo'
if ($combo -eq $null) { Write-Host 'FAIL: TransportCombo not found'; $p.Kill(); exit 1 }

$expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
$expand.Expand()
Start-Sleep -Milliseconds 800

# Popup items sometimes hang under the ComboBox element, sometimes in a separate popup
# window -- look in both places, always filtered by our process id.
$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = @($combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond))
if ($items.Count -eq 0) {
    $all = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $itemCond)
    foreach ($e in $all) { if ($e.Current.ProcessId -eq $p.Id) { $items += $e } }
}
Write-Host ('combo items: ' + $items.Count)

$target = $null
foreach ($e in $items) { if ($e.Current.Name -eq $telnetLabel) { $target = $e; break } }
if ($target -eq $null) {
    Write-Host 'FAIL: Telnet item not found in the combo'
} else {
    $sel = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Start-Sleep -Milliseconds 1000

    Save-Window $handle 'transport-telnet.png'

    $hostVisible = Is-Visible (Find-ById 'HostBox')
    $portVisible = Is-Visible (Find-ById 'PortCombo')
    Write-Host ('after switch -> HostBox visible: ' + $hostVisible + '  PortCombo visible: ' + $portVisible)
    if ($hostVisible -and -not $portVisible) { Write-Host 'PASS: transport switch works' }
    else { Write-Host 'FAIL: transport switch did not take effect' }
}

$p.Kill()
Write-Host 'test instance closed'
