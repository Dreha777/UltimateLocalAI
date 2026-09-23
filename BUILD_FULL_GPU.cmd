@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Ultimate Local AI v1.2 - расширенная GPU-сборка, включая Blackwell (если CUDA 12.8+)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-All.ps1" -BuildBlackwell
set ERR=%ERRORLEVEL%
pause
exit /b %ERR%
