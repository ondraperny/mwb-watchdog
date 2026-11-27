<#
  Removes MWB Keepalive for the current Windows user. Mouse Without Borders itself is not touched.
  install.ps1 copies this script into the program folder and registers it in
  Settings > Apps > Installed apps, which runs it when you click Uninstall.
  -Quiet: no message box at the end (for use from a terminal or script).
#>
param([switch]$Quiet)
$ErrorActionPreference = 'Stop'

# Current names first, then the former name (MWB Watchdog, version 1.0), so leftovers of either go.
$TaskNames     = 'MWB Keepalive', 'MWB Watchdog'
$AppDirs       = (Join-Path $env:LOCALAPPDATA 'MWBKeepalive'), (Join-Path $env:LOCALAPPDATA 'MWBWatchdog')
$Links         = foreach ($folder in 'Startup', 'Programs') {
                     foreach ($name in $TaskNames) { Join-Path ([Environment]::GetFolderPath($folder)) "$name.lnk" }
                 }
$UninstallKeys = 'MWBKeepalive', 'MWBWatchdog' | ForEach-Object { "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$_" }

function Show-Result([string]$Text, [string]$Icon) {
    if ($Quiet) { return }
    Add-Type -AssemblyName System.Windows.Forms
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true }
    [void][System.Windows.Forms.MessageBox]::Show($owner, $Text, 'MWB Keepalive', 'OK', $Icon)
    $owner.Dispose()
}

# This script usually runs from the folder it deletes.
Set-Location -LiteralPath $env:TEMP

try {
    foreach ($task in $TaskNames) {
        if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
            Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $task -Confirm:$false
            Write-Host "Scheduled task '$task' removed."
        }
    }
    foreach ($link in $Links) {
        if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force; Write-Host "Removed $link." }
    }

    Get-Process -Name 'MWBKeepalive', 'MWBWatchdog' -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue; Write-Host "Stopped MWB Keepalive process $($_.Id)."
    }
    # Older PowerShell-based version, if present.
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like '*mwb-watchdog.ps1*' -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Host "Stopped MWB Keepalive process $($_.ProcessId)." }

    foreach ($dir in $AppDirs) {
        if (Test-Path -LiteralPath $dir) {
            Start-Sleep -Milliseconds 500
            Remove-Item -LiteralPath $dir -Recurse -Force
            Write-Host "Removed $dir."
        }
    }
    foreach ($key in $UninstallKeys) {
        if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
    }

    Write-Host 'MWB Keepalive uninstalled.' -ForegroundColor Green
    Show-Result 'MWB Keepalive was removed. Mouse Without Borders itself was not changed.' 'Information'
} catch {
    Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    Show-Result "Removing MWB Keepalive failed:`n`n$($_.Exception.Message)" 'Error'
    exit 1
}
