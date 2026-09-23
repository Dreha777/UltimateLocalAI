
## Исправления v1.2.1

- Исправлена UTF-8 BOM в CMD-файлах, из-за которой cmd.exe мог выводить `echo ... не является командой`.
- Поиск .NET 8 SDK теперь проверяет x64 `C:\Program Files\dotnet\dotnet.exe` до PATH и не путает SDK с Runtime.
- Добавлен `CHECK_BUILD_ENV.cmd` для диагностики .NET/CMake/MSVC/CUDA/llama.cpp.

# Ultimate Local AI Portable v1.2.1 — b11060-ready

Портативный Windows-клиент локальных GGUF-моделей на `llama.cpp`.
Эта версия пересобрана под свежие исходники **llama.cpp b11060**, Xeon E3-1270 (AVX, без AVX2), GTX 1050 2 GB (Pascal, sm_61) и CUDA Toolkit 12.x.

## Главное изменение v1.2.1

Обычный `BUILD.cmd` больше не связывает Pascal-сборку с Blackwell:

- `cpu-avx` — обязательный backend для Xeon E3-1270/Sandy Bridge;
- `cpu-avx2` — обязательный backend для i7-6700/Skylake и новее;
- `cuda-pascal-avx` — отдельный необязательный backend `sm_61` для GTX 10xx, собирается только CUDA Toolkit 12.x;
- `cuda-modern-avx2` — отдельный необязательный backend для RTX 20/30/40/Hopper (`75;86;89;90`);
- `cuda-blackwell-avx2` — отдельный необязательный backend, строится только через `BUILD_FULL_GPU.cmd` при CUDA 12.8+.

Ошибка необязательного modern/Blackwell backend **не уничтожает успешную CPU/Pascal сборку и не останавливает публикацию GUI**.

## Твой ПК

```text
Intel Xeon E3-1270 3.4 GHz
4C / 8T
AVX: да
AVX2: нет
RAM: 16 GB
NVIDIA GTX 1050 2 GB
NVIDIA Driver: 582.66 WHQL
```

Для GTX 1050:

```text
CUDA architecture = 61 (Pascal)
CUDA Toolkit = 12.x (рекомендуется 12.8)
```

**Свежий драйвер NVIDIA 582.66 оставлять.** Версия драйвера и версия CUDA Toolkit — разные компоненты. CUDA Toolkit 12.x нужен для компиляции Pascal backend; после публикации Toolkit на целевом ПК не нужен.

## Куда положить llama.cpp b11060

Ожидается:

```text
vendor\llama.cpp\CMakeLists.txt
vendor\llama.cpp\ggml\CMakeLists.txt
vendor\llama.cpp\tools\
...
```

Если `CMakeLists.txt` уже лежит прямо в `vendor\llama.cpp`, `BUILD.cmd` использует локальные исходники и ничего не скачивает.

## Сборка одной командой

После установки Visual Studio 2022 (.NET Desktop + Desktop C++ + CMake + .NET 8 SDK) и, для GTX 1050, CUDA Toolkit 12.x:

```cmd
cd /d D:\1\UltimateLocalAI_v1
BUILD.cmd
```

Или просто двойной щелчок по `BUILD.cmd`.

Готовая программа:

```text
dist\UltimateLocalAI-Portable\UltimateLocalAI.exe
```

Для CPU-only:

```text
BUILD_CPU_ONLY.cmd
```

Для дополнительной попытки собрать Blackwell:

```text
BUILD_FULL_GPU.cmd
```

На твоём Xeon/GTX 1050 **BUILD_FULL_GPU.cmd не нужен**.

## Что проверяет BUILD.cmd

1. .NET SDK;
2. CMake из Visual Studio;
3. структуру локального `llama.cpp`;
4. наличие ожидаемых CMake-опций свежего llama.cpp;
5. CPU AVX backend;
6. CPU AVX2 backend;
7. наличие `nvcc` и версию CUDA Toolkit;
8. Pascal sm_61 при CUDA 12.x;
9. modern CUDA как отдельный необязательный backend;
10. self-contained WPF GUI;
11. наличие обязательных EXE в `dist`.

В конце выводится сводка, какие backend реально собраны.

## Безопасные стартовые настройки Xeon + GTX 1050 2 GB

```text
Backend: Auto
Context: 4096
CPU Threads: 6
Threads Batch: 8
GPU Layers: auto
Batch: 512
Micro-batch: 256
Flash Attention: auto
KV K: q8_0
KV V: q8_0
```

Это стартовый профиль. Реальную скорость и устойчивость конкретной GGUF проверяйте встроенным benchmark.

## Что умеет приложение

- выбор GGUF по любому пути;
- streaming-чат;
- локальное сохранение чатов;
- резервные копии и экспорт Markdown;
- русские настройки с комментариями;
- Auto/CPU/CUDA backend;
- автоматический CUDA -> CPU fallback;
- отдельный `llama-server.exe`, поэтому падение inference-процесса не должно закрывать GUI;
- `/health` перед разблокировкой чата;
- benchmark;
- reasoning/thinking controls;
- TXT/MD/CSV/JSON/XML/код/DOCX/XLSX/PDF;
- локальная база знаний;
- работа без облака и `--offline` для llama-server.

## Офлайн

Если `vendor\llama.cpp` уже заполнен вручную и Visual Studio/.NET SDK/CMake/CUDA Toolkit установлены локально, интернет для сборки не нужен.
После публикации на целевой машине не нужны Visual Studio, .NET SDK, CMake или CUDA Toolkit. Для GPU нужен только системный NVIDIA Driver.

## Важно

Абсолютную защиту от падения повреждённой GGUF, нехватки RAM/VRAM или ошибки драйвера гарантировать нельзя. Поэтому inference вынесен в отдельный процесс, CUDA имеет CPU fallback, а чаты сохраняются локально с резервированием.
