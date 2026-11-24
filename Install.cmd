@echo off
rem Double-click to install or update MWB Watchdog for this Windows user (no admin needed).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\install.ps1"
echo.
pause
