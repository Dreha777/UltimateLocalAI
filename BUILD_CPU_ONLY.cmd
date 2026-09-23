@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-All.ps1" -CpuOnly
set ERR=%ERRORLEVEL%
pause
exit /b %ERR%
