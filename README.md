# Ultimate Local AI v1.3 — GitHub Build Edition

Портативный Windows-клиент для локальных GGUF-моделей на `llama.cpp`.

**Разработчик:** Dreha777.

**Назначение:** локальный запуск GGUF-моделей на Windows с автоматическим определением CPU/GPU, выбором совместимого backend и безопасных runtime-параметров без обязательной ручной настройки.

## Главное

Собирать Visual Studio на своём ПК больше не требуется. GitHub Actions сам создаёт готовый portable-комплект.

**Actions → Build Windows Portable → Run workflow → Artifacts → UltimateLocalAI-Portable**.

Готовая папка содержит self-contained .NET 8 GUI и runtime'ы `llama.cpp`. На целевом ПК Visual Studio, CMake, .NET SDK и CUDA Toolkit не нужны. Для NVIDIA нужен только подходящий системный драйвер.

## Runtime Manager

В настройках программы:

- **Stable** — `llama.cpp b11060`, проверенная база;
- **Latest** — актуальный `master` `llama.cpp` на момент сборки, для новых GGUF-архитектур;
- **Custom** — собственная папка runtime без пересборки GUI.

Каждый runtime выбирает аппаратный backend автоматически:

- `cpu-avx` — старые Xeon с AVX без AVX2;
- `cpu-avx2` — i7-6700 и более новые x64 CPU;
- `cuda-pascal-avx` — Pascal `sm_61`, в частности GTX 1050/1060, совместимо со старым AVX Xeon;
- `cuda-modern-avx2` — Turing/Ampere/Ada (Latest, необязательная сборка);
- `cuda-blackwell-avx2` — Blackwell (Latest, необязательная сборка);
- `vulkan-avx` — универсальный Vulkan GPU fallback на AVX-only CPU (Latest);
- `vulkan-avx2` — универсальный Vulkan GPU fallback на AVX2 CPU (Latest).

Порядок Auto: совместимый NVIDIA CUDA → Vulkan → CPU AVX2 → CPU AVX.

## Модели

Модель не вшивается в программу. Нажмите **Выбрать GGUF** и укажите файл на любом диске. Приложение работает с GGUF-архитектурами, которые понимает выбранный runtime. Если новая модель не поддерживается Stable, переключите Runtime на Latest.

## Данные

Всё остаётся локально рядом с программой:

- `Data/config.json` — настройки;
- `Data/chats.json` — чаты;
- `Data/knowledge.json` — локальная база знаний;
- `Logs/` — диагностика.

## Целевая конфигурация проекта

Основной старый ПК: Xeon E3-1270 (AVX, без AVX2), 16 ГБ RAM, GTX 1050 2 ГБ Pascal. Для него GitHub Actions собирает отдельный CUDA `sm_61` backend через CUDA Toolkit 12.8.1.


## Справка

В интерфейсе доступно окно **«Справка / О программе»**. В Portable также находятся:
- `FIRST_START_RU.txt` — краткий первый запуск;
- `USER_GUIDE_RU.txt` — полное руководство пользователя;
- `THIRD_PARTY_NOTICES.md` — уведомления о сторонних компонентах.
