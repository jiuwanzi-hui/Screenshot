@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-ToolbarDiagnosis.ps1" -RenderingMode Hardware
pause
