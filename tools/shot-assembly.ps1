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

# args: <exe path> <output png name> [command key to select]
$exe = $args[0]
$outName = $args[1]
$wantKey = if ($args.Count -gt 2) { $args[2] } else { $null }

$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $repoRoot 'tools\shots'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$p = Start-Process -FilePath $exe -PassThru

$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) { Write-Host 'FAIL: process exited early'; exit 1 }
    $h = $p.MainWindowHandle
    if ($h -ne [IntPtr]::Zero) {
        $r = New-Object WinApi3+RECT
        [WinApi3]::GetWindowRect($h, [ref]$r) | Out-Null
        if (($r.Right - $r.Left) -gt 400) { $handle = $h; break }
    }
}
if ($handle -eq [IntPtr]::Zero) { Write-Host 'FAIL: no usable main window'; $p.Kill(); exit 1 }

[WinApi3]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 800

$app = [System.Windows.Automation.AutomationElement]::FromHandle($handle)

# select tab 0 (command assembly)
$tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)
$tabs = $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
$tabs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 700

# optionally select a command in the list
if ($wantKey -ne $null) {
    $listCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CommandList')
    $list = $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
    if ($list -eq $null) {
        Write-Host 'FAIL: CommandList not found'
    } else {
        # the ListBox item's own Name is the bound object's type name, so match the
        # key TextBlock inside the item template and walk up to its ListItem
        $keyCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $wantKey)
        $leaf = $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $keyCond)

        $hit = $null
        if ($leaf -ne $null) {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $node = $leaf
            while ($node -ne $null -and $node.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
                $node = $walker.GetParent($node)
            }
            $hit = $node
        }

        if ($hit -eq $null) { Write-Host ('FAIL: no list item containing ' + $wantKey) }
        else {
            $hit.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 500
            Write-Host ('selected item containing ' + $wantKey)

            $btnCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'GenButton')
            $btn = $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
            if ($btn -eq $null) { Write-Host 'FAIL: GenButton not found' }
            else {
                $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 600
            }
        }
    }
}

$r = New-Object WinApi3+RECT
[WinApi3]::GetWindowRect($handle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h2 = $r.Bottom - $r.Top
$bmp = New-Object Drawing.Bitmap $w, $h2
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $outDir $outName), [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Host ('OK ' + $outName + ' ' + $w + 'x' + $h2)

# dump the generated-frame box verbatim, so "offline behaviour unchanged" can be
# compared as text rather than as pixels (screenshots vary run to run: blinking caret)
$resCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ResultBox')
$res = $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $resCond)
if ($res -ne $null) {
    $tp = $null
    if ($res.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$tp)) {
        Write-Host ('=== ResultBox ===')
        Write-Host $tp.DocumentRange.GetText(-1)
        Write-Host ('=== /ResultBox ===')
    }
}

$p.Kill()
Write-Host 'closed'
