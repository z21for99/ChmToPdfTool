# CHM to PDF Converter

A self-contained Windows desktop application that converts Microsoft Compiled HTML Help (.chm) files to PDF, preserving the original table of contents as PDF bookmarks.

> **Download**: Get the ready-to-run EXE from the [latest release](https://github.com/z21for99/ChmToPdfTool/releases/latest). No installation required — just double-click.

## Table of Contents

- [Usage](#usage)
  - [GUI Mode](#gui-mode)
  - [Command Line Mode](#command-line-mode)
- [Requirements](#requirements)
- [Features](#features)
- [Architecture](#architecture)
  - [Pipeline Overview](#pipeline-overview)
  - [Extraction Engine](#extraction-engine)
  - [Rendering Engine](#rendering-engine)
  - [PDF Merging](#pdf-merging)
  - [Pause / Resume](#pause--resume)
- [Technical Details](#technical-details)
  - [Content Ordering](#content-ordering)
  - [Encoding Detection](#encoding-detection)
  - [DPI Awareness](#dpi-awareness)
- [Building from Source](#building-from-source)
- [Known Limitations](#known-limitations)
- [Third-Party Components](#third-party-components)
- [License](#license)

## Usage

### Quick Start

1. Download `CHM转PDF.exe` from the [Releases page](https://github.com/z21for99/ChmToPdfTool/releases/latest).
2. Double-click to launch.
3. Drag and drop a `.chm` file, or click **浏览** to browse.
4. Click **开始转换**.

### GUI Mode

1. Double-click `CHM转PDF.exe`.
2. Select a `.chm` file using the **Browse** button, or **drag and drop** a `.chm` file directly into the window.
3. Choose an output PDF path.
4. Click **开始转换** to begin.
5. Use **暂停** to pause (stops after the current page finishes printing) and **继续** to resume. Use **取消** to abort the entire task.
6. After completion, click **打开 PDF** to view the result.

### Command Line Mode

```powershell
CHM转PDF.exe --convert "input.chm" "output.pdf"
```

The CLI prints progress to stdout and errors to stderr. Exit code `0` means success.

## Requirements

- **Windows 10 or Windows 11**, x64 architecture.
- **Microsoft Edge** must be installed (included with Windows 10/11 by default).
- No .NET runtime, Python, Office, or Adobe Acrobat installation required — the executable is self-contained.

## Features

- Extracts and renders every HTML page inside the CHM archive.
- Preserves the original table of contents (`.hhc`) as PDF outline bookmarks, using the TOC labels as bookmark titles; falls back to the page `<title>`, then to alphabetical path order, when no TOC is found.
- Prints A4 pages by default (pages that declare their own CSS `@page` size keep it); without an explicit paper size Chromium would fall back to US Letter.
- Detects text encoding automatically (UTF-8, UTF-16 LE/BE, GBK / Code Page 936, and charset declarations inside HTML `<meta>` tags).
- Prints backgrounds, images, tables, and CSS styles from the original pages.
- Pause and resume during long conversions without losing progress.
- Cancel at any time; already-printed pages are discarded and partial output is cleaned up.
- Full offline processing — no data leaves the local machine. Temporary files are deleted automatically when conversion finishes (or is cancelled).

## Architecture

### Pipeline Overview

```
+-----------+       +------------------+       +----------------+       +--------------+
| .chm file | ----> | 7-Zip Extraction | ----> | Page Discovery | ----> | Edge CDP     |
| (input)   |       | (SharpSevenZip)  |       | + TOC Parsing  |       | (headless)   |
+-----------+       +------------------+       +----------------+       +--------------+
                                                                               |
                                                                               v
+-----------+       +------------------+       +------------------+
|   PDF     | <---- | PdfSharpCore     | <---- | Per-page PDF     |
| (output)  |       | (merge)          |       | (base64 via CDP) |
+-----------+       +------------------+       +------------------+
```

### Extraction Engine

Two extraction attempts are made, in order:

1. **7-Zip via SharpSevenZip** — the primary engine. It handles the vast majority of CHM files, including non-standard and large archives.
2. **Windows `hh.exe -decompile`** — a fallback invoked only when 7-Zip fails. This uses the built-in Windows HTML Help decompiler.

If both fail, a detailed error message identifies which engine threw which exception, aiding diagnosis of corrupted or encrypted CHM files.

### Rendering Engine

All pages are rendered by a **single, persistent headless Edge process** controlled via the **Chrome DevTools Protocol (CDP)** over WebSocket.

Why this matters:

- **Speed**: A standard "launch Edge per page" approach incurs 1–3 seconds of cold-start overhead per page. For a 200-page CHM, that alone wastes 3–10 minutes. By keeping one Edge process alive across the entire conversion, only one cold start is paid.
- **Input stability**: Each process launch briefly grabs Windows input focus. Repeated launches interrupt IME composition (typing, Chinese input methods, etc.). A single persistent process eliminates this entirely.

The CDP session flow on each page:

1. `Page.navigate` to the extracted HTML file.
2. Poll `document.readyState === 'complete'` until the DOM and sub-resources finish loading.
3. `Page.printToPDF` with `preferCSSPageSize: true` and `printBackground: true`. The PDF is received as a base64-encoded `data` field in the CDP response.
4. Write the decoded bytes to disk as an intermediate per-page PDF.

### PDF Merging

[PdfSharpCore](https://github.com/ststeiger/PdfSharpCore) merges all per-page PDFs into the final output. Each chapter's HTML `<title>` (or filename, if no title is present) is added as a PDF outline bookmark pointing to the first page of that chapter.

### Pause / Resume

A custom `PauseController` implements an async gate pattern:

- `Pause()` sets a `TaskCompletionSource` that `WaitIfPausedAsync()` awaits.
- `Resume()` completes the source, allowing the loop to continue.
- The gate is checked at the **top** of each page iteration, so the currently-printing page finishes before the pause takes effect. This avoids leaving a half-rendered page in the queue.

The pause/continue mechanism is fully unit-tested.

## Technical Details

### Content Ordering

CHM archives contain an `.hhc` file (HTML Help Contents) that encodes the chapter tree in `<OBJECT>` / `<param>` elements. The parser:

- Extracts all `Local` parameter values in document order.
- Strips URL fragments (`#`) and query strings (`?`).
- Normalizes forward/backward slashes.
- Removes `mk:@MSITStore:` protocol prefixes.
- Deduplicates entries (same path appearing in multiple TOC nodes).

When no `.hhc` exists, or when the TOC-referenced pages don't exist on disk, a fallback enumerates all `.htm`, `.html`, and `.xhtml` files under the extracted directory in alphabetical path order.

Both the parser and the discovery fallback are covered by unit tests.

### Encoding Detection

CHM files produced by different toolchains (English MSDN, Chinese documentation generators, etc.) encode internal HTML in various code pages. The encoding detection chain:

1. **BOM check** — UTF-8 (`EF BB BF`) or UTF-16 LE (`FF FE`).
2. **`<meta charset>` check** — parses the first 4 KB for `charset=...` and calls `Encoding.GetEncoding(...)`.
3. **Strict UTF-8 fallback** — `new UTF8Encoding(false, true)`; throws `DecoderFallbackException` on invalid bytes.
4. **GBK fallback** — `Encoding.GetEncoding(936)` as the last resort for Chinese-language CHM files.

### DPI Awareness

The WinForms application declares `PerMonitorV2` DPI awareness via the project property `<ApplicationHighDpiMode>`. The application manifest explicitly avoids legacy `dpiAware` settings, preventing blurry text on high-DPI displays.

## Building from Source

**Prerequisites**:

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022, VS Code, or any C# editor

**Steps**:

```powershell
# Restore dependencies and build
dotnet build ChmToPdfTool/ChmToPdfTool.csproj -c Release

# Run unit tests
dotnet run --project ChmToPdfTool/ChmToPdfTool.Tests/ChmToPdfTool.Tests.csproj -c Release

# Publish self-contained single-file EXE (win-x64)
dotnet publish ChmToPdfTool/ChmToPdfTool.csproj -c Release -r win-x64 --self-contained true -o publish -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true
```

**Tech stack**:

| Component | Technology |
|-----------|-----------|
| UI Framework | Windows Forms (.NET 8) |
| CHM Extraction | SharpSevenZip (7-Zip engine) |
| HTML Rendering | Microsoft Edge (headless, Chrome DevTools Protocol) |
| PDF Merging | PdfSharpCore |
| Encoding Detection | System.Text.Encoding.CodePages |
| Async Primitives | System.Threading.Channels, SemaphoreSlim, ConcurrentDictionary |
| Testing | C# console test runner (no external test framework) |

## Known Limitations

- **ActiveX controls, legacy scripts, and popups** inside CHM pages are not executed or rendered by Edge's headless PDF printer.
- **Pages that load external network resources** (CSS, images, fonts) may render incompletely since the tool runs offline.
- **Encrypted or DRM-protected CHM files** cannot be decompiled by either 7-Zip or `hh.exe`.
- **Extremely large single pages** (>100 MB after HTML parsing) may exceed Edge's memory budget in headless mode and time out.
- **Print fidelity** depends on Edge's PDF generation engine. CSS `@page` directives, complex `flexbox`/`grid` layouts, and some web fonts may render differently than in a browser window.

## Third-Party Components

| Component | License | Source |
|-----------|---------|--------|
| SharpSevenZip | LGPL-3.0+ | [github.com/JeremyAnsel/SharpSevenZip](https://github.com/JeremyAnsel/SharpSevenZip) |
| 7-Zip engine | LGPL / BSD / unRAR | [7-zip.org](https://www.7-zip.org/) |
| PdfSharpCore | MIT-like | [github.com/ststeiger/PdfSharpCore](https://github.com/ststeiger/PdfSharpCore) |
| System.Text.Encoding.CodePages | MIT | [github.com/dotnet/runtime](https://github.com/dotnet/runtime) |

See [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt) for full license details.

## License

MIT. Do whatever you want with it.
