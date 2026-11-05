@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-ToolbarSystemTrace.ps1"
echo.
pause
