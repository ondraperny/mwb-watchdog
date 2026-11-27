# MWB Keepalive: technical details

Setup steps are in the [README](../README.md). MWB below means Mouse Without Borders.

## What the installer does

- compiles `mwb-keepalive.cs` into `%LOCALAPPDATA%\MWBKeepalive\MWBKeepalive.exe`, using the C# compiler that ships with Windows (.NET Framework 4.x), with the tray badge as its icon
- registers a scheduled task, **MWB Keepalive**, that starts it at logon and restarts it if it crashes (it falls back to the Startup folder if the task can't be created)
- adds a Start menu entry (`MWBKeepalive.exe --start`), which starts it or says it's already running
- registers it in Settings → Apps → Installed apps; Uninstall there runs `uninstall.ps1`, copied into the program folder
- makes the tray icon visible on the taskbar the first time
- prints this PC's name, IP and MAC address, and warns about PCs in the MWB layout that have no IP mapping

There is no automatic update: the installer builds whatever `mwb-keepalive.cs` is next to it. Running it again from a newer copy of the folder replaces the installed version, including *MWB Watchdog* (this program's name until version 1.1, whose log it keeps) and the older PowerShell-based version.

## From a terminal

| What | How |
|---|---|
| Current state | `"%LOCALAPPDATA%\MWBKeepalive\MWBKeepalive.exe" --status` |
| Reconnect now | `"%LOCALAPPDATA%\MWBKeepalive\MWBKeepalive.exe" --reconnect` |
| What it did | `%LOCALAPPDATA%\MWBKeepalive\keepalive.log` (only changes are logged) |
| Install or update without questions | `powershell -ExecutionPolicy Bypass -File files\install.ps1 -Yes` |
| Remove without the dialog | `powershell -File "%LOCALAPPDATA%\MWBKeepalive\uninstall.ps1" -Quiet` |

MWB Keepalive reads the list of PCs from MWB's settings, so when you add a PC, the existing PCs pick it up automatically.

## How it works

Every 2 seconds it asks MWB, through MWB's own settings pipe (the one PowerToys Settings uses), which PCs are connected. It also checks that Windows still has an open MWB connection.

For each PC that is not connected:
1. It first gives MWB a few seconds to fix the drop itself. It leaves MWB alone while MWB reports "Resolving", "Connecting" or "Handshaking", or while MWB's pipe is busy with a reconnect.
2. It then knocks on that PC's MWB port. If there is no answer (the PC is off or asleep), it does nothing and knocks again 3 seconds later.
3. If the PC answers, it tells MWB to reconnect, which is the same thing the hotkey does.

MWB's reconnect resets all of its connections, so reconnects are rate-limited per PC: 15 s, 30 s, 60 s, 120 s, then 300 s if one doesn't stick. When this PC wakes from sleep, MWB Keepalive forces one reconnect, because MWB can wrongly believe its old connection is still alive.

It changes nothing in MWB's settings. It uses about 14 MB of memory (as shown in Task Manager) and about 0.1% of one CPU core.

## Tested (Windows 11, PowerToys 0.100.2)

- **Wi-Fi outage of several minutes:** MWB marked the link "Error" and did not retry. MWB Keepalive reconnected it 11 s after Wi-Fi returned.
- **Simulated sleep/wake:** MWB Keepalive forced a reconnect, and MWB was connected again 7 s later.
- **Manual reconnects** (hotkey or `--reconnect`): MWB Keepalive stays out of the way.

## Limitations

- It relies on two PowerToys internals: the `MouseWithoutBorders/SettingsSync` pipe (methods `RequestMachineSocketStateAsync` and `Reconnect`) and, as a fallback, the reconnect event used by the hotkey. If a PowerToys update changes these, the tray icon turns red or the log shows errors, and MWB behaves as it did without MWB Keepalive.
- MWB's retry timings are hard-coded and can't be changed in its settings (checked in the PowerToys v0.100.2 source).
- Reconnecting isn't instant. MWB Keepalive waits about 5–10 seconds before stepping in, so MWB can fix quick drops itself. MWB's own reconnect then takes a few seconds; in testing it was about 8 s, mostly a slow name lookup through a VPN's DNS server.

## Making a release

The release zip is exactly the committed files (`.gitattributes` keeps CRLF line endings and leaves out git's own files). Bump the version in `mwb-keepalive.cs` (`AssemblyVersion`/`AssemblyFileVersion`) first, then:

```
git tag v1.1.0 && git push origin main v1.1.0
git archive --format=zip -o dist/MWB-Keepalive.zip v1.1.0
gh release create v1.1.0 dist/MWB-Keepalive.zip --title "MWB Keepalive 1.1.0" --notes "What changed"
```

The README links to `releases/latest/download/MWB-Keepalive.zip`, so it always serves the newest release.

## Credits

PowerToys and Mouse Without Borders are made by Microsoft and are open source (MIT license). MWB Keepalive talks to Mouse Without Borders through the same local channels PowerToys itself uses, worked out from the PowerToys source code. No PowerToys code is included.
