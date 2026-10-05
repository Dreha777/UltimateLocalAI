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

## WpfMath / XAML-Math

- Package: WpfMath 2.1.0
- Project: XAML-Math / WPF-Math
- License: project code/resources under MIT; bundled font assets include Knuth License and SIL Open Font License (OFL) terms as documented by XAML-Math
- Purpose: local rendering of LaTeX mathematical formulae in chat.

## PDFsharp / MigraDoc

- Package: PDFsharp-MigraDoc-WPF 6.2.4
- Project: PDFsharp / MigraDoc
- License: MIT
- Purpose: local export of chat history to PDF and Word-compatible RTF; PDF rendering uses Windows font resolution with a local fallback.

## Open XML SDK

- Package: DocumentFormat.OpenXml 3.5.1
- Project: Open XML SDK
- License: MIT
- Purpose: creation of native Microsoft Word DOCX chat exports without requiring Microsoft Office.

## PdfPig

- Package: PdfPig 0.1.6
- Project: PdfPig
- License: Apache-2.0
- Purpose: page-aware text extraction from text-layer PDF documents for the local RAG index.

## CUDA / Vulkan

The build system can use NVIDIA CUDA Toolkit and LunarG Vulkan SDK to compile GPU-enabled llama.cpp backends. These SDKs are governed by their own vendor license terms.

## GGUF models

GGUF model files are not part of Ultimate Local AI. Each model may have its own license, usage restrictions and attribution requirements.

## Project-specific code

The Ultimate Local AI application, hardware-selection logic, UI integration, packaging scripts and project configuration are maintained as the Ultimate Local AI project by Яхлов Андрей Васильевич.

This notice is informational and does not replace the original license texts of third-party projects.
