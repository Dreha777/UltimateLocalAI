@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo.
echo ==============================================
echo  Ultimate Local AI Portable v1.2
echo  b11060-ready / Xeon AVX + GTX 1050 Pascal
echo ==============================================
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-All.ps1" -Clean
set ERR=%ERRORLEVEL%
echo.
if not "%ERR%"=="0" (
  echo Сборка завершилась с ошибкой. Ничего не переустанавливайте наугад.
  echo Пришлите последние строки окна, начиная с первой ошибки.
) else (
  echo Готово. Откройте dist\UltimateLocalAI-Portable\UltimateLocalAI.exe
)
pause
exit /b %ERR%
