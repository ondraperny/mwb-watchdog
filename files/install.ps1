<#
  Installs (or updates) MWB Keepalive for the current Windows user. Run it via ..\Install.cmd.
  - Compiles mwb-keepalive.cs with the C# compiler built into Windows (.NET Framework 4.x).
  - Registers a scheduled task that starts it (tray icon) at every logon.
  - Adds a Start menu entry (to start it again after Exit) and an entry in
    Settings > Apps > Installed apps (to uninstall it).
  - Replaces MWB Watchdog (this program's name until version 1.1) and keeps its log.
  No admin rights needed. It says what it will do and asks first; if it is already
  installed, it offers to install again (update/repair) or to uninstall.
  -Yes: no questions, just install or update (for scripts).
#>
param([switch]$Yes)
$ErrorActionPreference = 'Stop'

$TaskName      = 'MWB Keepalive'
$Source        = Join-Path $PSScriptRoot 'mwb-keepalive.cs'
$Uninstaller   = Join-Path $PSScriptRoot 'uninstall.ps1'
$AppDir        = Join-Path $env:LOCALAPPDATA 'MWBKeepalive'
$Exe           = Join-Path $AppDir 'MWBKeepalive.exe'
$MwbSettings   = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\MouseWithoutBorders\settings.json'
$StartupLink   = Join-Path ([Environment]::GetFolderPath('Startup')) 'MWB Keepalive.lnk'
$StartMenuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'MWB Keepalive.lnk'
$UninstallKey  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MWBKeepalive'

# The same program under its former name, MWB Watchdog (version 1.0).
$OldTaskName     = 'MWB Watchdog'
$OldAppDir       = Join-Path $env:LOCALAPPDATA 'MWBWatchdog'
$OldStartupLink  = Join-Path ([Environment]::GetFolderPath('Startup')) 'MWB Watchdog.lnk'
$OldStartMenuLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'MWB Watchdog.lnk'
$OldUninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MWBWatchdog'

function Step([string]$Text) { Write-Host "-> $Text" -ForegroundColor Cyan }

function Format-Version([string]$Text) {
    try { $v = [version]$Text; '{0}.{1}.{2}' -f $v.Major, $v.Minor, [Math]::Max($v.Build, 0) } catch { $Text }
}

# Asks until one of $Keys is typed; gives up (returns $null) on missing input.
function Ask([string]$Prompt, [string[]]$Keys) {
    for ($i = 0; $i -lt 5; $i++) {
        $answer = Read-Host $Prompt
        if ($null -eq $answer) { return $null }
        $answer = $answer.Trim().ToUpperInvariant()
        if ($Keys -contains $answer) { return $answer }
        Write-Host ('   Please type ' + ($Keys -join ' or ') + '.')
    }
    return $null
}

Write-Host ''
Write-Host '=== MWB Keepalive installer ===' -ForegroundColor White
Write-Host ''

# Only one installer at a time (e.g. Install.cmd double-clicked twice).
$installerLock = New-Object System.Threading.Mutex($false, 'Local\MWBKeepalive-Installer')
try { $gotLock = $installerLock.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $gotLock = $true }
if (-not $gotLock) {
    Write-Host 'The installer is already running in another window. Finish or close that one first.' -ForegroundColor Yellow
    exit 1
}

foreach ($f in $Source, $Uninstaller) {
    if (-not (Test-Path -LiteralPath $f)) { throw "$(Split-Path $f -Leaf) must be in the same folder as this installer ($PSScriptRoot)." }
}

$newVersion = '?'
if ((Get-Content -Raw -LiteralPath $Source) -match 'AssemblyFileVersion\("([\d.]+)"\)') { $newVersion = Format-Version $Matches[1] }
$installed = (Test-Path -LiteralPath $AppDir) -or [bool](Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)
$oldNameInstalled = (Test-Path -LiteralPath $OldAppDir) -or [bool](Get-ScheduledTask -TaskName $OldTaskName -ErrorAction SilentlyContinue)

if (-not $Yes) {
    if ($installed) {
        $oldVersion = 'an older version'
        if (Test-Path -LiteralPath $Exe) { $oldVersion = Format-Version (Get-Item -LiteralPath $Exe).VersionInfo.FileVersion }
        Write-Host "MWB Keepalive is already installed on this PC (installed: $oldVersion, this folder: $newVersion)."
        Write-Host ''
        Write-Host '  [I] Install again - replace it with the version in this folder (update or repair)'
        Write-Host '  [U] Uninstall     - remove it (Mouse Without Borders itself is not changed)'
        Write-Host '  [C] Cancel        - change nothing'
        Write-Host ''
        $choice = Ask 'Your choice (I/U/C)' @('I', 'U', 'C')
        if ($choice -eq 'U') {
            Write-Host ''
            & $Uninstaller -Quiet
            exit $LASTEXITCODE
        }
        if ($choice -ne 'I') { Write-Host 'Nothing was changed.'; exit 0 }
    } else {
        Write-Host "This installs MWB Keepalive $newVersion for your Windows user. It will:"
        Write-Host '  - build the program on this PC from the included source code'
        Write-Host '  - start it now and at every sign-in (tray icon)'
        Write-Host '  - add it to the Start menu, and to Settings > Apps > Installed apps for uninstalling'
        if ($oldNameInstalled) {
            Write-Host '  - replace MWB Watchdog, which is installed on this PC (the same program under its former name)'
        }
        Write-Host 'No admin rights are needed, and Mouse Without Borders settings are not changed.'
        Write-Host ''
        if ((Ask 'Install now? (Y/N)' @('Y', 'N')) -ne 'Y') { Write-Host 'Nothing was changed.'; exit 0 }
    }
    Write-Host ''
}
if (-not (Test-Path -LiteralPath $MwbSettings)) {
    Write-Warning 'Mouse Without Borders settings were not found. Install PowerToys and set up Mouse Without Borders on this PC; MWB Keepalive will simply wait until then.'
}

# 1. Build first, so a failed build leaves any existing installation untouched.
Step 'Building MWBKeepalive.exe with the C# compiler included in Windows'
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $csc) { throw 'The .NET Framework 4 C# compiler (csc.exe) was not found. It is part of Windows 10/11; is .NET Framework 4.8 disabled?' }

$build = Join-Path $env:TEMP ('MWBKeepalive-build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $build -Force | Out-Null
$manifest = Join-Path $build 'app.manifest'
@'
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity version="1.0.0.0" name="MWBKeepalive"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security><requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
      <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
    </requestedPrivileges></security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings><dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware></windowsSettings>
  </application>
</assembly>
'@ | Set-Content -LiteralPath $manifest -Encoding UTF8

$cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/win32manifest:$manifest",
             '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
             '/r:System.Web.Extensions.dll')
$plainExe = Join-Path $build 'MWBKeepalive-noicon.exe'
$output = & $csc @cscArgs "/out:$plainExe" $Source 2>&1
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $plainExe)) {
    $output | Where-Object { $_ -match 'error' } | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
    throw 'Build failed - nothing was changed.'
}

# The program draws its own tray icon, so let it write that icon to a file, then build again
# with it embedded (shown in the Start menu and Installed apps). Not essential - skip on failure.
$builtExe = Join-Path $build 'MWBKeepalive.exe'
$iconFile = Join-Path $build 'MWBKeepalive.ico'
$p = Start-Process -FilePath $plainExe -ArgumentList "--write-icon `"$iconFile`"" -Wait -PassThru
if ($p.ExitCode -eq 0 -and (Test-Path -LiteralPath $iconFile)) {
    & $csc @cscArgs "/out:$builtExe" "/win32icon:$iconFile" $Source 2>&1 | Out-Null
}
if (-not (Test-Path -LiteralPath $builtExe)) {
    Write-Host '   (could not add the program icon; using the default one)'
    Copy-Item -LiteralPath $plainExe -Destination $builtExe
}

# 2. Stop and remove any previous version (including MWB Watchdog and the older PowerShell-based one).
Step 'Removing any previous version'
foreach ($task in $TaskName, $OldTaskName) {
    if (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $task -Confirm:$false
    }
}
foreach ($link in $StartupLink, $OldStartupLink, $OldStartMenuLnk) {
    if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -Force }
}
Get-Process -Name 'MWBKeepalive', 'MWBWatchdog' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
    Where-Object { $_.CommandLine -like '*mwb-watchdog.ps1*' -and $_.ProcessId -ne $PID } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

# 3. Put the program in a fixed place in the user profile.
Step "Installing to $AppDir"
New-Item -ItemType Directory -Path $AppDir -Force | Out-Null
Copy-Item -LiteralPath $builtExe -Destination $Exe -Force
Copy-Item -LiteralPath $Uninstaller -Destination (Join-Path $AppDir 'uninstall.ps1') -Force
Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue

# Under its former name the program lived in MWBWatchdog: keep its log, then remove the rest.
if (Test-Path -LiteralPath $OldAppDir) {
    $oldLog = Join-Path $OldAppDir 'watchdog.log'
    $newLog = Join-Path $AppDir 'keepalive.log'
    if ((Test-Path -LiteralPath $oldLog) -and -not (Test-Path -LiteralPath $newLog)) {
        Copy-Item -LiteralPath $oldLog -Destination $newLog
    }
    Remove-Item -LiteralPath $OldAppDir -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $OldUninstallKey) { Remove-Item -LiteralPath $OldUninstallKey -Recurse -Force }

Step 'Adding "MWB Keepalive" to the Start menu (starts it again if you exit it)'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($StartMenuLink)
$lnk.TargetPath = $Exe
$lnk.Arguments = '--start'
$lnk.WorkingDirectory = $AppDir
$lnk.Description = 'Keeps Mouse Without Borders connected'
$lnk.Save()

Step 'Adding it to Settings > Apps > Installed apps (to uninstall it from there)'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
New-Item -Path $UninstallKey -Force | Out-Null
$entry = @{
    DisplayName     = 'MWB Keepalive'
    DisplayIcon     = $Exe
    DisplayVersion  = Format-Version (Get-Item -LiteralPath $Exe).VersionInfo.FileVersion
    Comments        = 'Keeps PowerToys Mouse Without Borders connected'
    InstallLocation = $AppDir
    InstallDate     = (Get-Date).ToString('yyyyMMdd')
    UninstallString = "`"$powershell`" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$AppDir\uninstall.ps1`""
}
foreach ($name in $entry.Keys) {
    New-ItemProperty -LiteralPath $UninstallKey -Name $name -Value $entry[$name] -PropertyType String -Force | Out-Null
}
$sizeKb = [int][Math]::Ceiling((Get-ChildItem -LiteralPath $AppDir -File | Measure-Object Length -Sum).Sum / 1KB)
foreach ($dword in @{ NoModify = 1; NoRepair = 1; EstimatedSize = $sizeKb }.GetEnumerator()) {
    New-ItemProperty -LiteralPath $UninstallKey -Name $dword.Key -Value $dword.Value -PropertyType DWord -Force | Out-Null
}

# 4. Start it at every logon, in the user's session (needed for the tray icon).
$mode = $null
try {
    Step 'Registering scheduled task (starts at logon, restarts if it ever crashes)'
    $user      = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action    = New-ScheduledTaskAction -Execute $Exe -WorkingDirectory $AppDir
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
                    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
        -Description 'Keeps PowerToys Mouse Without Borders connected (auto-reconnect, tray icon).' | Out-Null
    Start-ScheduledTask -TaskName $TaskName
    $mode = 'task'
} catch {
    Write-Warning "Could not create the scheduled task ($($_.Exception.Message)). Using the Startup folder instead."
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($StartupLink)
    $lnk.TargetPath = $Exe
    $lnk.WorkingDirectory = $AppDir
    $lnk.Description = 'MWB Keepalive'
    $lnk.Save()
    Start-Process -FilePath $Exe -WorkingDirectory $AppDir
    $mode = 'startup'
}

# 5. Windows 11 hides new tray icons under the ^ arrow. Show ours on the taskbar the first
#    time (if the user later hides it, that choice is kept on reinstall).
Step 'Making the tray icon visible on the taskbar'
$iconKey = 'HKCU:\Control Panel\NotifyIconSettings'
$promoted = $false
for ($i = 0; $i -lt 20 -and -not $promoted; $i++) {
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path $iconKey)) { continue }
    foreach ($entry in Get-ChildItem $iconKey) {
        $v = Get-ItemProperty -LiteralPath $entry.PSPath
        if ($v.ExecutablePath -and ($v.ExecutablePath -ieq $Exe)) {
            if ($null -eq $v.PSObject.Properties['IsPromoted']) {
                New-ItemProperty -LiteralPath $entry.PSPath -Name IsPromoted -PropertyType DWord -Value 1 -Force | Out-Null
            }
            $promoted = $true
        }
    }
}
if (-not $promoted) { Write-Host '   (could not find the icon entry - if it is hidden, drag it out from under the ^ arrow)' }

# 6. Show the result and what other PCs need to know about this one.
Start-Sleep -Seconds 2
Write-Host ''
Write-Host '=== Current state ===' -ForegroundColor White
& $Exe --status | Out-Host

Write-Host ''
Write-Host '=== This PC (for the router reservation and other PCs) ===' -ForegroundColor White
$lan = $null
foreach ($r in (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric, InterfaceMetric)) {
    $ad = Get-NetAdapter -InterfaceIndex $r.ifIndex -Physical -ErrorAction SilentlyContinue
    if ($ad -and $ad.Status -eq 'Up') { $lan = $ad; break }
}
if ($lan) {
    $ip = (Get-NetIPAddress -InterfaceIndex $lan.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1).IPAddress
    Write-Host ("  Name: {0}   Network: {1}   IP: {2}   MAC: {3}" -f $env:COMPUTERNAME, $lan.Name, $ip, $lan.MacAddress)
    Write-Host ''
    Write-Host '  On the OTHER PCs, add this line to Mouse Without Borders > IP address mapping:'
    Write-Host ("      {0} {1}" -f $env:COMPUTERNAME, $ip) -ForegroundColor Yellow
    Write-Host '  In the router, reserve that IP for that MAC address.'
} else {
    Write-Host '  Could not determine the main network adapter.'
}

# Warn about PCs in this PC's MWB layout that have no IP mapping.
if (Test-Path -LiteralPath $MwbSettings) {
    try {
        $p = ([IO.File]::ReadAllText($MwbSettings) | ConvertFrom-Json).properties
        $mapText = ''
        if ($p.Name2IP -and $p.Name2IP.value) { $mapText = [string]$p.Name2IP.value }
        $mapped = @($mapText -split "[\r\n]+" | ForEach-Object { (($_.Trim() -split '\s+')[0]).ToUpperInvariant() } | Where-Object { $_ })
        $missing = @($p.MachineMatrixString | Where-Object { $_ -and $_.Trim() -and $_.Trim().ToUpperInvariant() -ne $env:COMPUTERNAME.ToUpperInvariant() -and $mapped -notcontains $_.Trim().ToUpperInvariant() })
        if ($missing.Count) {
            Write-Host ''
            Write-Warning ("No IP address mapping on this PC for: {0}. Add '<name> <ip>' lines in Mouse Without Borders settings for reliable reconnects." -f ($missing -join ', '))
        }
    } catch { }
}

Write-Host ''
if ($mode -eq 'task') { Write-Host "Installed. It starts automatically when you log in (Task Scheduler: '$TaskName')." -ForegroundColor Green }
else { Write-Host 'Installed. It starts automatically when you log in (Startup folder).' -ForegroundColor Green }
Write-Host 'Tray icon (round, with a mouse): green = connected, orange = reconnecting, grey = other PC off, red = problem.'
Write-Host 'Click it for details, "Reconnect now" and the log.'
Write-Host 'If you exit it, start it again from the Start menu: "MWB Keepalive".'
Write-Host 'To remove it: Settings > Apps > Installed apps > MWB Keepalive > Uninstall.'
