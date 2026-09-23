@echo off
chcp 65001 >nul
cd /d "%~dp0"
if exist "vendor\llama.cpp\CMakeLists.txt" (
  echo llama.cpp уже находится внутри vendor\llama.cpp
  pause
  exit /b 0
)
where git >nul 2>nul
if errorlevel 1 (
  echo Не найден Git for Windows.
  pause
  exit /b 1
)
if exist "vendor\llama.cpp" rmdir /s /q "vendor\llama.cpp"
git clone --depth 1 --branch b11060 https://github.com/ggml-org/llama.cpp.git "vendor\llama.cpp"
pause
