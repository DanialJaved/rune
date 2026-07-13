# Folio

*A fast, free, modern PDF reader for Windows.* (Working title — final name TBD before v1.)

Windows has never had a PDF reader that is fast **and** lightweight **and** modern-looking at the same time. SumatraPDF is legendary for speed but wears a 2009 UI; Edge and Acrobat are heavy; Okular's Windows port doesn't feel native. Folio aims to combine:

- the **speed** of SumatraPDF / Zathura — instant startup, 60 fps scrolling, tiny footprint
- the **clean modern UI** of macOS Preview / GNOME Papers — Fluent design, Mica, dark mode
- (eventually) the **feature depth** of Okular — annotations, forms, signatures

Free and open source, in the spirit of SumatraPDF and Flow Launcher.

## Tech

| Layer | Choice |
|---|---|
| UI | WinUI 3 (Windows App SDK 2.x), C# / .NET 10 |
| PDF engine | [PDFium](https://pdfium.googlesource.com/pdfium/) (Chrome's renderer, Apache 2.0) via [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) |
| Architecture | Thin P/Invoke interop → single-threaded render queue + LRU tile cache → virtualized canvas |

```
src/
  Folio.App/            WinUI 3 shell (views, view-models)
  Folio.Engine/         document services, render scheduler, cache, search
  Folio.PdfiumInterop/  P/Invoke bindings over pdfium.dll
tests/
  Folio.Tests/          xUnit tests against a corpus of sample PDFs
```

## Building

Requires the .NET 10 SDK on Windows. No Visual Studio needed:

```
dotnet build src/Folio.App/Folio.App.csproj -p:Platform=x64
```

The output `Folio.exe` is unpackaged and self-contained — just run it.

## Roadmap

- **M1** — open a PDF, render pages ✅
- **M2** — viewer core: virtualized scroll, zoom, tile rendering, fast cold open ✅
- **M3** — tabs, thumbnails, outline, links, back/forward, session restore ✅
- **M4** — text selection, copy, find-in-document with highlights ✅
- **M5** — night mode, command palette (Ctrl+K), keyboard-first UX, printing ✅
- **M6** — v1 release: MSIX, file association, portable zip *(next)*
- **v2** — annotations, form filling, signatures, page organizing, more formats
