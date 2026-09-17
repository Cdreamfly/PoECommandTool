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

$exe = 'C:\Users\ymz\source\repos\WpfApp1\bin\ReviewBuild\WpfApp1.exe'
$outDir = 'C:\Users\ymz\source\repos\WpfApp1\tools\shots'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$p = Start-Process -FilePath $exe -PassThru

# poll until a real main window shows up (a 160x28 splash handle appears first)
$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $p.Refresh()
    if ($p.HasExited) { Write-Host 'FAIL: process exited early'; exit 1 }
    $h = $p.MainWindowHandle
    if ($h -ne [IntPtr]::Zero) {
        $r = New-Object WinApi2+RECT
        [WinApi2]::GetWindowRect($h, [ref]$r) | Out-Null
        if (($r.Right - $r.Left) -gt 400) { $handle = $h; break }
    }
}
if ($handle -eq [IntPtr]::Zero) { Write-Host 'FAIL: no usable main window'; $p.Kill(); exit 1 }

[WinApi2]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 800

$app = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
$tabCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::TabItem)
$tabs = $app.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tabCond)
Write-Host ('tabs found: ' + $tabs.Count)

# page 4 = serial read/write
$sel = $tabs[3].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
$sel.Select()
Start-Sleep -Milliseconds 900

$r = New-Object WinApi2+RECT
[WinApi2]::GetWindowRect($handle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left
$h = $r.Bottom - $r.Top
$bmp = New-Object Drawing.Bitmap $w, $h
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $outDir 'serial_info.png'), [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Host ('OK serial_info.png ' + $w + 'x' + $h)

$p.Kill()
Write-Host 'closed'
