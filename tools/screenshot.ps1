Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinApi {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
}
"@

$exe = 'C:\Users\ymz\source\repos\WpfApp1\bin\Debug\PoECommandTool.exe'
$outDir = 'C:\Users\ymz\source\repos\WpfApp1\tools\shots'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Save-Window([IntPtr]$handle, [string]$name) {
    $r = New-Object WinApi+RECT
    [WinApi]::GetWindowRect($handle, [ref]$r) | Out-Null
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

$p.Refresh()
$handle = $p.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { Write-Host 'FAIL: no main window handle'; exit 1 }

[WinApi]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 600

# capture every tab, then the serial page maximized
$app = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)
$tabs = $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
Write-Host ('tabs found: ' + $tabs.Count)

for ($i = 0; $i -lt $tabs.Count; $i++) {
    $sel = $tabs[$i].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Start-Sleep -Milliseconds 700
    Save-Window $handle ('tab' + $i + '.png')
}

# on the serial page, click the maximize button
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MaximizeChartButton')
$btn = $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($btn -eq $null) {
    Write-Host 'FAIL: maximize button not found'
} else {
    $pattern = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Start-Sleep -Milliseconds 1500
    Save-Window $handle 'tab3_maximized.png'
}

$p.Kill()
Write-Host 'test instance closed'
