# Third-party notices

**Ultimate Local AI developer:** Яхлов Андрей Васильевич

Ultimate Local AI uses third-party open-source components. Their original license terms remain applicable.

## llama.cpp

- Project: llama.cpp
- Upstream: https://github.com/ggml-org/llama.cpp
- Organization: ggml-org
- License: MIT
- Purpose: local GGUF inference backend and llama-server runtime.

The Portable build may include runtimes built from a pinned stable revision and from the upstream master revision used by the corresponding GitHub Actions build.

When redistributing llama.cpp-derived binaries or source, retain the applicable upstream license and notices.

## Microsoft .NET / WPF

Ultimate Local AI uses .NET 8 / WPF for the Windows graphical application and self-contained runtime packaging. These components are governed by their respective Microsoft/open-source license terms.

## CUDA / Vulkan

The build system can use NVIDIA CUDA Toolkit and LunarG Vulkan SDK to compile GPU-enabled llama.cpp backends. These SDKs are governed by their own vendor license terms.

## GGUF models

GGUF model files are not part of Ultimate Local AI. Each model may have its own license, usage restrictions and attribution requirements.

## Project-specific code

The Ultimate Local AI application, hardware-selection logic, UI integration, packaging scripts and project configuration are maintained as the Ultimate Local AI project by Яхлов Андрей Васильевич.

This notice is informational and does not replace the original license texts of third-party projects.
