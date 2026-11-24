# MWB Watchdog

Use one keyboard and mouse across several Windows PCs, without the connection silently dropping.

Microsoft's free **PowerToys** includes **Mouse Without Borders**. You move the cursor off the edge of one PC's screen and it continues on the next PC, with the keyboard and clipboard following. That works well until the connection drops (Wi-Fi blip, router restart, sleep, the other PC rebooting). Then it often stays disconnected until you press Win+Ctrl+Alt+R. MWB Watchdog is a small add-on that notices the drop and reconnects as soon as the other PC is reachable again.

**Why it's needed:** Mouse Without Borders retries only when Windows reports that the network went offline and came back. On PCs with a VPN or virtual network adapters (Mullvad, Tailscale, VMware…), Windows almost never reports that, so a dropped connection stays dropped. There's no setting to change this.

## Setup

On each PC:

1. **Install PowerToys:** from the Microsoft Store, or run `winget install Microsoft.PowerToys`.
2. **Turn on Mouse Without Borders:** PowerToys Settings → Mouse Without Borders → enable it.
3. **Connect the PCs:** on the first PC, select *New key*. On each other PC, enter that key and the first PC's name, then select *Connect*. Arrange the PCs in the layout to match your desk.
4. **Add IP address mappings:** in the Mouse Without Borders settings, under *IP address mapping*, add a line `<PC name> <IP address>` for every *other* PC, e.g. `LAPTOP 192.168.1.10`. Finding PCs by IP is faster and more reliable than by name. `Install.cmd` (step 6) prints the line for the PC it runs on.
5. **Optional: reserve the IPs in your router.** Give each PC a fixed IP (a "DHCP reservation") so the mappings never go stale; `Install.cmd` prints the MAC address the router asks for. If you skip this and a PC's IP changes, update its mapping line.
6. **Install MWB Watchdog:** download [MWB-Watchdog.zip](../../releases/latest/download/MWB-Watchdog.zip) from the latest release, extract it, and double-click `Install.cmd`. No admin rights needed. Windows may warn that the file came from the internet; allow it to run.

To add a PC later, do the same on it and add its mapping line on the existing PCs.

## Use

- **Status:** tray icon. Green means connected, orange reconnecting, grey the other PC is off, red a problem. Click it for details.
- **Start it again after Exit:** Start menu → *MWB Watchdog*. It also starts by itself at every sign-in.
- **Update:** download the latest release and run its `Install.cmd`; when it's already installed, it offers to update or uninstall. It doesn't update itself.
- **Remove:** Settings → Apps → Installed apps → *MWB Watchdog* → Uninstall.

Technical details: [files/DETAILS.md](files/DETAILS.md).

## Disclaimer

MWB Watchdog is an unofficial tool, not made by or affiliated with Microsoft. It relies on undocumented parts of PowerToys, so a PowerToys update can break it. If that happens, Mouse Without Borders behaves as it would without the watchdog.

It's free and provided as is, without warranty of any kind (see [LICENSE](LICENSE)). You install and use it at your own risk, and the author isn't responsible for any problems or damage it causes. You can check what it does yourself: the installer builds the program on your PC from the source code in `files/`. It needs no admin rights, connects only to the PCs listed in your Mouse Without Borders settings, and doesn't change those settings.
