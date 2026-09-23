@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo === Проверка локального llama.cpp ===
if not exist "vendor\llama.cpp\CMakeLists.txt" (
  echo [ОШИБКА] Нет vendor\llama.cpp\CMakeLists.txt
  pause
  exit /b 1
)
if not exist "vendor\llama.cpp\ggml\CMakeLists.txt" (
  echo [ОШИБКА] Нет vendor\llama.cpp\ggml\CMakeLists.txt
  pause
  exit /b 1
)
findstr /C:"LLAMA_BUILD_SERVER" "vendor\llama.cpp\CMakeLists.txt" >nul || (
  echo [ОШИБКА] Не найдена LLAMA_BUILD_SERVER
  pause
  exit /b 1
)
echo [OK] Структура llama.cpp подходит для BUILD.cmd.
echo CMakeLists: %CD%\vendor\llama.cpp\CMakeLists.txt
pause
