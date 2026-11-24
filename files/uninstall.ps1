<#
  Removes MWB Watchdog for the current Windows user. Mouse Without Borders itself is not touched.
  install.ps1 copies this script into the program folder and registers it in
  Settings > Apps > Installed apps, which runs it when you click Uninstall.
  -Quiet: no message box at the end (for use from a terminal or script).
#>
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'

$TaskName      = 'MWB Watchdog'
$AppDir        = Join-Path $env:LOCALAPPDATA 'MWBWatchdog'
$StartupLink   = Join-Path ([Environment]::GetFolderPath('Startup')) 'MWB Watchdog.lnk'
$StartMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'MWB Watchdog.lnk'
$UninstallKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MWBWatchdog'

function Show-Result([string]$Text, [string]$Icon) {
    if ($Quiet) { return }
    Add-Type -AssemblyName System.Windows.Forms
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
    [void][System.Windows.Forms.MessageBox]::Show($owner, $Text, 'MWB Watchdog', 'OK', $Icon)
    $owner.Dispose()
}

# This script usually runs from the folder it deletes.
Set-Location -LiteralPath $env:TEMP

try {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host 'Scheduled task removed.'
    }
    foreach ($link in $StartupLink, $StartMenuLink) {
        if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force; Write-Host "Removed $link." }
    }

    Get-Process -Name 'MWBWatchdog' -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; Write-Host "Stopped watchdog process $($_.Id)."
    }
    # Older PowerShell-based version, if present.
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like '*mwb-watchdog.ps1*' -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Host "Stopped watchdog process $($_.ProcessId)." }

    if (Test-Path -LiteralPath $AppDir) {
        Start-Sleep -Milliseconds 500
        Remove-Item -LiteralPath $AppDir -Recurse -Force
        Write-Host "Removed $AppDir."
    }
    if (Test-Path -LiteralPath $UninstallKey) { Remove-Item -LiteralPath $UninstallKey -Recurse -Force }

    Write-Host 'MWB Watchdog uninstalled.' -ForegroundColor Green
    Show-Result 'MWB Watchdog was removed. Mouse Without Borders itself was not changed.' 'Information'
} catch {
    Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    Show-Result "Removing MWB Watchdog failed:`n`n$($_.Exception.Message)" 'Error'
    exit 1
}
