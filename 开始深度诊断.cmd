@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Monitor.ps1" -Deep
if errorlevel 1 pause
