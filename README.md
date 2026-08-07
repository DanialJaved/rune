<div align="center">

# ᚱ Rune

**A fast, free, modern PDF reader for Windows.**

[![Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Rune%20PDF%20Reader-0078D4?logo=microsoftstore&logoColor=white)](https://apps.microsoft.com/detail/9NH37840QDM6)
[![CI](https://github.com/DanialJaved/rune/actions/workflows/ci.yml/badge.svg)](https://github.com/DanialJaved/rune/actions/workflows/ci.yml)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![Release](https://img.shields.io/github/v/release/DanialJaved/rune)](https://github.com/DanialJaved/rune/releases)

**[Install from the Microsoft Store →](https://apps.microsoft.com/detail/9NH37840QDM6)**

</div>

Windows never had a PDF reader that is fast **and** lightweight **and** modern-looking at the same time. SumatraPDF is legendary for speed but wears a 2009 UI; Edge and Acrobat are heavy; Okular's Windows port doesn't feel native. Rune combines:

- the **speed** of SumatraPDF / Zathura — instant open, smooth scrolling through 1,000-page documents, strict memory budget
- the **look and feel of GNOME Papers** — a slim single header bar, a floating zoom control, a big-thumbnail sidebar — rebuilt natively on Windows 11 (Mica, dark mode, tabs in the title bar)
- a **keyboard-first** workflow — command palette, full arrow/vim navigation, a shortcuts overlay (`F1`), every action reachable without the mouse

| Dark | Night mode (inverted pages) |
|---|---|
| ![Rune in dark mode](docs/screenshot-dark.png) | ![Rune night mode](docs/screenshot-night.png) |

## Features

- Tabs in the title bar (Chrome-style), lazy-loaded — background tabs cost nothing until shown
- Continuous virtualized scrolling with tile-based progressive rendering (blurry-fast → crisp), rock-steady even while searching and scrolling a 1,000-page document at once
- Zoom 10–640% at the cursor from a floating zoom pill, fit-width / fit-page, rotation
- **Sidebar** (open by default, `F9`) with a switcher for **thumbnails / chapters / bookmarks**; internal & web links; back/forward history
- Full keyboard navigation — arrows scroll and page, `PageUp`/`PageDown`, `Home`/`End` (plus optional vim keys)
- Text selection & copy, find-in-document with highlight-all and hit stepping
- **Form filling**: click a field and type — text boxes, dropdowns and checkboxes, saved back into the PDF. Standard AcroForm documents only; XFA forms (some tax and government PDFs) say so plainly instead of silently ignoring your typing, and form JavaScript doesn't run, so auto-calculating fields won't recalculate
- **Flatten**: bake annotations and filled fields into the page so they can't be edited back out
- **Signature details**: for a digitally signed PDF, shows the signer's stated reason, time and format, and whether the signature covers the whole file. Rune does **not** verify signatures — it has no cryptography and never claims a signature is valid
- **Annotation toolbar**: pen, highlighter, note, sign and eraser sit together in the middle of the header; picking one opens its colours, size and opacity right beneath it. The highlighter works in a single gesture — just drag across the text — and still writes real PDF markup, so highlights stay attached to the words and other readers understand them
- **Sign a document**: draw your signature with the mouse or pen, or **photograph one on paper and import it — Rune removes the paper for you**, so only the ink lands on the page instead of a white rectangle over your document. It runs entirely on your machine: no upload, no online service. A semi-transparent preview follows the cursor so you can see exactly where it will land (scroll to resize it first), click to place, and drag it afterwards if it's not quite right. Saved signatures are reusable and stay on your machine. This is a *visible* signature — ink on the page, like signing a printout — not a cryptographic one
- **Annotations**: highlight, underline, strikeout, sticky notes, and **freehand pen/ink** — saved as standard PDF annotations any reader can see (`Ctrl+H` highlight, `Ctrl+E` pen, `Esc` to put the tool away, right-click menu, `Ctrl+S` / `Ctrl+Shift+S`)
- **Page editing** in the thumbnail sidebar: multi-select, drag to reorder, `Delete`, copy/cut/paste pages (`Ctrl+C`/`X`/`V`, works across open tabs), or drop a PDF onto the sidebar to insert its pages
- **Undo / redo** for annotations and page edits (`Ctrl+Z` / `Ctrl+Y`)
- **Bookmarks** (`Ctrl+B`): name a page and jump back later; saved per document
- **Presentation mode** (`F5`): fullscreen, one page at a time, arrows / space / click to advance
- **Keyboard shortcuts overlay** (`F1`)
- **Night mode**: GPU-inverted page colors for dark-room reading (`Ctrl+I`)
- **Recent-documents homepage**: a clean grid of first-page thumbnails of your last files
- Command palette (`Ctrl+K`) with fuzzy filtering and go-to-page
- Session restore: reopens your tabs at the exact scroll position
- Pinch-to-zoom (touch/touchpad) and `Ctrl`+scroll, zoom at the cursor
- Printing with live preview and page ranges
- Opens damaged PDFs gracefully; 4 GB-file streaming without loading into memory

The interface follows GNOME Papers' proportions — one compact header row, with the rest tucked into a single menu — but is built entirely from native Windows 11 controls.

## Keyboard shortcuts

Press `F1` in the app for the full list. The essentials:

| Action | Keys |
|---|---|
| Open / close tab | `Ctrl+O` / `Ctrl+W` |
| Scroll / screen up-down | `↑` `↓` / `PgUp` `PgDn`, `Space` |
| Previous / next page | `←` / `→` |
| First / last page | `Home` / `End` |
| Find / next / previous | `Ctrl+F` / `F3` / `Shift+F3` |
| Command palette / shortcuts | `Ctrl+K` / `F1` |
| Zoom in / out / 100% / fit page / fit width | `Ctrl++` / `Ctrl+-` / `Ctrl+1` / `Ctrl+0` / `Ctrl+2` |
| Night mode / sidebar | `Ctrl+I` / `F9` |
| Rotate right / left | `Ctrl+R` / `Ctrl+Shift+R` |
| Presentation / bookmark page | `F5` / `Ctrl+B` |
| Highlight / pen / save / save as | `Ctrl+H` / `Ctrl+E` / `Ctrl+S` / `Ctrl+Shift+S` |
| Copy / cut / paste (text or pages) | `Ctrl+C` / `Ctrl+X` / `Ctrl+V` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` |
| Print / properties | `Ctrl+P` / `Ctrl+D` |

Vim-style keys (`j k h l`, `gg`/`G`, `p`/`n`) can be enabled in Settings. Right-click a selection for underline/strikeout, or anywhere to add a note. Pen colour and width appear when you click the pen. In the thumbnail sidebar, `Ctrl+C`/`X`/`V` copy/cut/paste **pages**; elsewhere they act on selected text.

## Install

### [![Get Rune PDF Reader from the Microsoft Store](https://img.shields.io/badge/Get%20it%20from%20the-Microsoft%20Store-0078D4?style=for-the-badge&logo=microsoftstore&logoColor=white)](https://apps.microsoft.com/detail/9NH37840QDM6)

**This is the way to install Rune.** One click, automatic updates, it registers as a PDF handler, and Microsoft signs the package — so there's no certificate step and nothing for SmartScreen or Smart App Control to warn about. Works from the Store app or [on the web](https://apps.microsoft.com/detail/9NH37840QDM6).

**Portable:** if you want Rune without the Store — on a USB stick, or on a machine where you can't install anything — grab `rune-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/DanialJaved/rune/releases), extract anywhere, run `Rune.exe`. No installation, no registry.

> The portable build is **not code-signed**: machines with Smart App Control enabled will block it, and SmartScreen may warn on first run ("More info → Run anyway"). It also doesn't update itself — check back here, or use the Store build. And it isn't size-optimized; it carries the full self-contained .NET and Windows App SDK runtimes.

## Tech

| Layer | Choice |
|---|---|
| UI | WinUI 3 (Windows App SDK 2.x), C# / .NET 10 |
| PDF engine | [PDFium](https://pdfium.googlesource.com/pdfium/) (Chrome's renderer, BSD-3-Clause/Apache-2.0) via [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) |
| Rendering | Win2D virtualized canvas ← LRU tile cache ← single render thread (PDFium is not thread-safe) ← thin P/Invoke |

```
src/
  Rune.App/            WinUI 3 shell: tabs, viewer control, palette, print
  Rune.Engine/         document services, render scheduler, layout, search, state
  Rune.PdfiumInterop/  P/Invoke bindings over pdfium.dll
tests/
  Rune.Tests/          xUnit suite against a generated PDF corpus
```

## Building

.NET 10 SDK on Windows — no Visual Studio required:

```
dotnet build src/Rune.App/Rune.App.csproj -p:Platform=x64
dotnet test tests/Rune.Tests/Rune.Tests.csproj
```

The debug build is an unpackaged self-contained exe — just run it.

## Roadmap

**Next:** typed signatures in a handwriting font (draw and import already ship), resizing a signature after placing it, colour and size for form-filling text, page extraction to a new file, more formats (ePub, CBZ), code signing for the portable build, smaller packages.

**Known limits:** form JavaScript needs a V8-enabled PDFium build; text selection, annotation, form filling **and the whole signature flow** are disabled while the view is rotated; a signature can be moved after placing but not resized; signature *validation* would need a cryptography stack Rune doesn't ship.

## License

[GPLv3](LICENSE) — free forever, and derivatives stay free. Built on PDFium (BSD-3-Clause/Apache-2.0), Win2D (MIT), the Windows App SDK, and .NET (MIT); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

### A note on the Microsoft Store

Rune is published on the Microsoft Store as **[Rune PDF Reader](https://apps.microsoft.com/detail/9NH37840QDM6)**, where it is covered by the Store's Standard Application License Terms. Those terms are generally considered incompatible with the GPL, so this deserves an explicit word:

Rune's own source is written entirely by its copyright holder, and its dependencies are permissively licensed (PDFium is BSD-3-Clause/Apache-2.0, Win2D and .NET are MIT) — there is no third-party copyleft code in it. As the sole copyright holder I can distribute my own work under whatever terms I choose, and I choose to make it available both under the GPLv3 here and through the Store for people who want automatic updates and one-click installation.

**This does not change anything about the GPLv3 grant.** The source in this repository is, and will remain, GPLv3: you may use, study, modify and redistribute it under those terms. The Store listing is an additional distribution channel, not a replacement, and every release is always available here as a portable download.
