Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = 'C:\Users\ymz\source\repos\WpfApp1\bin\Release\WpfApp1.exe'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
$p.Refresh()
$app = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
foreach ($id in @('ZoomAutoButton','ClearChartButton','PauseChartButton','DeviceInfoCopyButton')) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $btn = $app.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($btn -eq $null) { Write-Host ($id + ': NOT FOUND') }
    else {
        $r = $btn.Current.BoundingRectangle
        Write-Host ($id + ': found name=[' + $btn.Current.Name + '] offscreen=' + $btn.Current.IsOffscreen + ' bounds x=' + $r.X + ' w=' + $r.Width)
    }
}
$p.Kill()
