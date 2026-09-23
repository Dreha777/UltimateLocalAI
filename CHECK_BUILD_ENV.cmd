@echo off
setlocal
cd /d "%~dp0"
echo ==============================================
echo Ultimate Local AI - build environment check
echo ==============================================
echo.
echo [1/5] .NET hosts and SDKs
if exist "%ProgramFiles%\dotnet\dotnet.exe" (
  echo x64 dotnet: %ProgramFiles%\dotnet\dotnet.exe
  "%ProgramFiles%\dotnet\dotnet.exe" --list-sdks
) else (
  echo x64 dotnet NOT FOUND in Program Files
)
echo.
where dotnet 2>nul
dotnet --list-sdks 2>nul
echo.
echo [2/5] CMake
where cmake 2>nul
cmake --version 2>nul
echo.
echo [3/5] MSBuild / Visual Studio
where msbuild 2>nul
where cl 2>nul
echo.
echo [4/5] CUDA compiler
where nvcc 2>nul
nvcc --version 2>nul
echo.
echo [5/5] llama.cpp source
if exist "vendor\llama.cpp\CMakeLists.txt" (echo llama.cpp CMakeLists: OK) else (echo llama.cpp CMakeLists: MISSING)
if exist "vendor\llama.cpp\ggml\CMakeLists.txt" (echo ggml CMakeLists: OK) else (echo ggml CMakeLists: MISSING)
echo.
echo Done. If build still fails, send a photo or copy all output above.
pause
