# Verifies SSH LOGIN through the real UI: the password must travel from the PasswordBox
# into NetworkSettings into SshTransport, the host-key dialog must appear, and the shell
# output must come back and land in the log.
#
# The password is a SCRIPT PARAMETER and is never written to this file.
#
# HARD RULES: never use global mouse/keyboard; always filter windows by process id.
# Keep this file PURE ASCII (PowerShell 5.1 reads BOM-less UTF-8 as GBK).
# Assertions use ASCII substrings of the log only (the connect line carries "<user>@<host>:<port>",
# and the remote shell's prompt carries its own hostname), because the UI text is Chinese and
# this file must stay ASCII.
#
# NOTE: param() must be the FIRST statement in the file -- anything before it is a parse error.
#
# Usage:  shot-ssh-login.ps1 -Target <host> -User <name> -Password <pw> [-Port 22]
# Target/User/Password carry NO defaults on purpose: this file is public, so no real
# host address, account name or password may be baked into it.

param(
    [Parameter(Mandatory=$true)][string]$Target,
    [Parameter(Mandatory=$true)][string]$User,
    [Parameter(Mandatory=$true)][string]$Password,
    [int]$Port = 22
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class WinApi4 {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr hDlg, int id);

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);

    /// <summary>Give back every visible top-level window of the process, as "handle|class" lines.
    /// Filtering by process id is mandatory -- matching "the window that is not the main one"
    /// once captured the user's editor and closed it.</summary>
    public static string[] WindowsOf(uint pid) {
        var found = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid) return true;
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            found.Add(h.ToInt64().ToString() + "|" + sb.ToString());
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }
}
"@

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
        $r = New-Object WinApi4+RECT
        [WinApi4]::GetWindowRect($h, [ref]$r) | Out-Null
        if (($r.Right - $r.Left) -gt 300) { $handle = $h; break }
    }
    Start-Sleep -Milliseconds 500
}
if ($handle -eq [IntPtr]::Zero) { Write-Host 'FAIL: no main window handle'; $p.Kill(); exit 1 }
[WinApi4]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 800

$app = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

function Find-ById([string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Set-Text($element, [string]$value, [string]$label) {
    if ($element -eq $null) { Write-Host ('  set ' + $label + ': ELEMENT NOT FOUND'); return $false }
    try {
        $vp = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue($value)
        Write-Host ('  set ' + $label + ': ok')
        return $true
    } catch {
        Write-Host ('  set ' + $label + ': FAILED -> ' + $_.Exception.Message)
        return $false
    }
}

# Sanity: a control that certainly exists.
if ((Find-ById 'PortCombo') -eq $null) { Write-Host 'FAIL: probe broken'; $p.Kill(); exit 1 }

# --- switch to SSH ---
$combo = Find-ById 'TransportCombo'
$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
Start-Sleep -Milliseconds 800
$itemCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = @($combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond))
$sshItem = $null
foreach ($e in $items) { if ($e.Current.Name -eq 'SSH') { $sshItem = $e; break } }
if ($sshItem -eq $null) { Write-Host 'FAIL: SSH item not found'; $p.Kill(); exit 1 }
$sshItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 900

# --- fill in the endpoint. The password goes through the PasswordBox, which is the
# --- whole point: it must reach the transport from there.
Write-Host '--- filling fields ---'
Set-Text (Find-ById 'HostBox') $Target 'host' | Out-Null
Set-Text (Find-ById 'NetPortBox') ([string]$Port) 'port' | Out-Null
Set-Text (Find-ById 'UserBox') $User 'user' | Out-Null
$okPw = Set-Text (Find-ById 'PasswordInput') $Password 'password'

# --- connect ---
$open = Find-ById 'OpenCloseButton'
Write-Host ('--- clicking connect (label=' + $open.Current.Name + ') ---')
$open.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

# --- the host-key confirmation is a MODAL Win32 dialog. Dismiss it by posting IDOK (1)
# --- straight to that window handle -- targeted, not global input.
$dialog = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    foreach ($w in [WinApi4]::WindowsOf([uint32]$p.Id)) {
        $parts = $w -split '\|'
        if ([int64]$parts[0] -ne $handle.ToInt64() -and $parts[1] -eq '#32770') {
            $dialog = [IntPtr][int64]$parts[0]
            break
        }
    }
    if ($dialog -ne [IntPtr]::Zero) { break }
}
if ($dialog -ne [IntPtr]::Zero) {
    Write-Host '--- host key dialog appeared; accepting it ---'
    [WinApi4]::PostMessage($dialog, 0x0111, [IntPtr]1, [IntPtr]::Zero) | Out-Null   # WM_COMMAND / IDOK
} else {
    Write-Host '--- no host key dialog seen (may have been trusted already) ---'
}

Start-Sleep -Seconds 6

# --- read the log (tab 3 is realised only when selected) ---
$tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)
$tabs = $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
if ($tabs.Count -ge 4) {
    $tabs[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 800
}

$r = New-Object WinApi4+RECT
[WinApi4]::GetWindowRect($handle, [ref]$r) | Out-Null
$bmp = New-Object Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $outDir 'ssh-login.png'), [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

$log = Find-ById 'SerialLogBox'
$text = ''
if ($log -ne $null) {
    $text = $log.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
}

Write-Host '--- log (last lines) ---'
foreach ($l in (($text -split "`n") | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 8)) {
    Write-Host ('  ' + $l.Trim())
}

$p.Refresh()
Write-Host '--- verdict ---'
Write-Host ('password accepted by PasswordBox: ' + $okPw)
Write-Host ('host key dialog seen:             ' + ($dialog -ne [IntPtr]::Zero))
Write-Host ('log mentions the target:          ' + $text.Contains($Target))
Write-Host ('log mentions cloudsvr (remote output): ' + $text.Contains('cloudsvr'))
Write-Host ('process still alive:              ' + (-not $p.HasExited))

if ($text.Contains($Target) -and $text.Contains('cloudsvr') -and -not $p.HasExited) {
    Write-Host 'PASS: SSH login works through the UI'
} else {
    Write-Host 'FAIL: SSH login through the UI did not complete'
}

# --- disconnect ---
$open2 = Find-ById 'OpenCloseButton'
if ($open2 -ne $null) { $open2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 3
$p.Refresh()
Write-Host ('after disconnect, still alive: ' + (-not $p.HasExited))

# --- reconnect WITHOUT re-typing the password ---
# This is the regression the user hit: disconnect used to clear the password box, so the
# next connect sent an empty password and the device answered "Permission denied (password)."
#
# Take the baseline AFTER the disconnect has been logged. Taking it before lets output still
# trickling in from the first connection's device-info read inflate the count, so the check
# passes for the wrong reason (that false positive actually happened). Both the connect and
# the disconnect lines carry "<user>@<host>:<port>", which is ASCII -- count those.
$connectMark = 'ssh ' + $User + '@'
$t = $text
for ($i = 0; $i -lt 25; $i++) {
    Start-Sleep -Milliseconds 400
    $t = $log.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
    if (([regex]::Matches($t, [regex]::Escape($connectMark))).Count -ge 2) { break }
}
$before = ([regex]::Matches($t, [regex]::Escape($connectMark))).Count
Write-Host ('connect/disconnect lines before reconnect: ' + $before)

$open3 = Find-ById 'OpenCloseButton'
Write-Host ('--- reconnecting without re-typing the password (label=' + $open3.Current.Name + ', enabled=' + $open3.Current.IsEnabled + ') ---')
$open3.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

# The host key is trusted for the whole process now, so NO dialog should appear here.
# If one does, the trust is not remembered -- report that explicitly rather than hanging.
$dialogAgain = $false
$t2 = $t
$after = $before
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 400
    foreach ($w in [WinApi4]::WindowsOf([uint32]$p.Id)) {
        $parts = $w -split '\|'
        if ([int64]$parts[0] -ne $handle.ToInt64() -and $parts[1] -eq '#32770') { $dialogAgain = $true }
    }
    if ($dialogAgain) { break }
    $t2 = $log.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
    $after = ([regex]::Matches($t2, [regex]::Escape($connectMark))).Count
    if ($after -gt $before) { break }
}
if (-not $dialogAgain) {
    $t2 = $log.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
    $after = ([regex]::Matches($t2, [regex]::Escape($connectMark))).Count
}
Write-Host ('connect lines before/after reconnect: ' + $before + ' -> ' + $after)
Write-Host '--- log tail after reconnect ---'
foreach ($l in (($t2 -split "`n") | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -Last 6)) {
    Write-Host ('  ' + $l.Trim())
}

$p.Refresh()
Write-Host '--- reconnect verdict ---'
if ($dialogAgain) { Write-Host 'FAIL: host key dialog appeared AGAIN on reconnect -- the trust is not remembered' }
elseif ($p.HasExited) { Write-Host 'FAIL: process died during reconnect' }
elseif ($after -gt $before) { Write-Host 'PASS: reconnect works without re-typing the password' }
else { Write-Host 'FAIL: reconnect produced no new connect line' }

$p.Kill()
Write-Host 'test instance closed'
