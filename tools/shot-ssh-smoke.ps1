Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinApi3 {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
"@

# SSH smoke test WITHOUT a real device.
#
# The point is NOT to prove SSH works -- it is to prove the twelve transitive DLLs that
# SSH.NET drags in actually LOAD on this machine. Point SSH at 127.0.0.1:1 (nothing
# listens there) and require the outcome to be a connection error, never an assembly
# load error. A missing DLL shows up as "Could not load file or assembly ..." -- which
# would only ever be discovered on a clean machine, at the first SSH connect.
#
# HARD RULES: never use global mouse/keyboard; always filter elements by process id.
# Keep this file PURE ASCII (PowerShell 5.1 reads BOM-less UTF-8 as GBK).

$repoRoot = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repoRoot 'bin\Release\PoECommandTool.exe'
$outDir = Join-Path $repoRoot 'tools\shots'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5

$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 30; $i++) {
    $p.Refresh()
    if ($p.HasExited) { Write-Host ('FAIL: exited code=' + $p.ExitCode); exit 1 }
    $h = $p.MainWindowHandle
    if ($h -ne [IntPtr]::Zero) {
        $r = New-Object WinApi3+RECT
        [WinApi3]::GetWindowRect($h, [ref]$r) | Out-Null
        if (($r.Right - $r.Left) -gt 300) { $handle = $h; break }
    }
    Start-Sleep -Milliseconds 500
}
if ($handle -eq [IntPtr]::Zero) { Write-Host 'FAIL: no main window handle'; $p.Kill(); exit 1 }
[WinApi3]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 800

$app = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

function Find-ById([string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Set-Text($element, [string]$value) {
    $vp = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($value)
}

# Sanity: a control that certainly exists. If missing, the probe is broken.
if ((Find-ById 'PortCombo') -eq $null) { Write-Host 'FAIL: probe broken'; $p.Kill(); exit 1 }

# --- switch the transport combo to SSH ---
$combo = Find-ById 'TransportCombo'
$expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
$expand.Expand()
Start-Sleep -Milliseconds 800
$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = @($combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond))
$target = $null
foreach ($e in $items) { if ($e.Current.Name -eq 'SSH') { $target = $e; break } }
if ($target -eq $null) { Write-Host ('FAIL: SSH item not found (items=' + $items.Count + ')'); $p.Kill(); exit 1 }
$sel = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
Start-Sleep -Milliseconds 900

# --- fill in an endpoint that is guaranteed to refuse ---
Set-Text (Find-ById 'HostBox') '127.0.0.1'
Set-Text (Find-ById 'NetPortBox') '1'
Set-Text (Find-ById 'UserBox') 'smoke'

$open = Find-ById 'OpenCloseButton'
Write-Host ('connect button label: ' + $open.Current.Name)
$open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 6

# --- read the in-tab log ---
# The log box lives inside the "connection & read/write" tab (index 3), and a WPF
# TabControl only realises the selected tab's content -- so select it first,
# otherwise the element simply does not exist in the automation tree.
$tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)
$tabs = $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
Write-Host ('tabs: ' + $tabs.Count)
if ($tabs.Count -ge 4) {
    $tabs[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800
}

$log = Find-ById 'SerialLogBox'
if ($log -eq $null) { Write-Host 'FAIL: SerialLogBox not found'; $p.Kill(); exit 1 }
$tp = $log.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
$text = $tp.DocumentRange.GetText(-1)

$r = New-Object WinApi3+RECT
[WinApi3]::GetWindowRect($handle, [ref]$r) | Out-Null
$bmp = New-Object Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $outDir 'ssh-smoke.png'), [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

$p.Refresh()
Write-Host ('process still alive: ' + (-not $p.HasExited))

# The UI text is Chinese, but this file must stay pure ASCII. Build the two markers we
# need from code points instead: "connect failed" (our own log prefix) and
# "could not load file or assembly" (the .NET load-error message).
$connectFailed = [string]([char]0x8FDE + [char]0x63A5 + [char]0x64CD + [char]0x4F5C + [char]0x5931 + [char]0x8D25)
$assemblyWord  = [string]([char]0x7A0B + [char]0x5E8F + [char]0x96C6)   # "assembly"

# A load failure always names the assembly, and that name is ASCII.
$loadErrorMarker = ($text -match 'Could not load file or assembly') -or
                   ($text -match 'FileNotFoundException') -or
                   ($text -match 'FileLoadException') -or
                   ($text -match '\bRenci\.') -or
                   ($text -match 'BouncyCastle') -or
                   ($text -match 'Version=') -or
                   ($text.Contains($assemblyWord))

$attempted = $text.Contains($connectFailed)

$lines = ($text -split "`n") | Where-Object { $_.Trim().Length -gt 0 }
Write-Host '--- last log lines ---'
foreach ($l in ($lines | Select-Object -Last 5)) { Write-Host ('  ' + $l.Trim()) }
Write-Host '--- verdict ---'
Write-Host ('connect was attempted and failed cleanly: ' + $attempted)
if ($loadErrorMarker) { Write-Host 'FAIL: assembly load error -- SSH.NET dependencies are not resolvable' }
elseif ($p.HasExited) { Write-Host 'FAIL: process died during SSH connect' }
elseif (-not $attempted) { Write-Host 'INCONCLUSIVE: no connect-failure line found in the log' }
else { Write-Host 'PASS: no assembly load error; the connect failed for a link reason' }

$p.Kill()
Write-Host 'test instance closed'
