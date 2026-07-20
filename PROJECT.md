# Rune — Project Reference

> A single-file brain-dump so a fresh session (human or AI) can understand
> and continue this project without re-deriving context. Last updated for
> **v0.4.0** (2026-07-20).

---

## 1. What this is

**Rune** is a free, open-source (**GPLv3**) PDF reader for Windows. It was
started 2026-07-12 under the working title *Folio* and renamed to *Rune* at
v0.1 (English-recognizable, Nordic "writing" connotation, avoids the crowded
"Folio" trademark space).

**Goal:** the speed of SumatraPDF/Zathura **and** the modern look of macOS
Preview / GNOME Papers **and** a lightweight footprint — no existing Windows
viewer is all three at once. Keyboard-first, in the spirit of SumatraPDF and
Flow Launcher.

- **Repo (local):** `C:\Users\djkho\code\pdf reader`
- **GitHub:** https://github.com/DanialJaved/rune (public; `gh` CLI is
  authenticated as `DanialJaved`)
- **Owner/dev:** Danial Javed — new to C#/.NET, Rust, and web stacks; explain
  non-obvious .NET concepts (P/Invoke, async, XAML binding) while building.

---

## 2. Tech stack (decisions are settled — don't re-litigate)

| Layer | Choice | Notes |
|---|---|---|
| Language/runtime | **C# / .NET 10** | |
| UI | **WinUI 3** (Windows App SDK **2.2.0**) | Fluent, Mica, dark mode |
| PDF engine | **PDFium** via `bblanchon.PDFium.Win32` NuGet (152.x) | Chrome's renderer; BSD-3-Clause/Apache-2.0 |
| Canvas | **Win2D** (`Microsoft.Graphics.Win2D` 1.4.0) | virtualized `CanvasVirtualControl` |
| MVVM helpers | `CommunityToolkit.Mvvm` 8.4.2 | used lightly |
| Build model | Unpackaged self-contained `.exe` for dev; MSIX at release | |

**Built from scratch, not a fork.** PDFium provides the proven renderer so
"from scratch" only meant the app shell + interop.

---

## 3. Solution layout

`Rune.slnx` (new `.slnx` solution format) with four projects:

```
src/
  Rune.PdfiumInterop/   Thin P/Invoke bindings over pdfium.dll
    NativeMethods.cs      Raw [DllImport] signatures (fpdfview/doc/text/annot/save)
    PdfiumNative.cs       Public facade so the engine never touches DllImports
    PdfiumLibrary.cs      Global FPDF_InitLibrary + the serialization lock
    FileAccessAdapter.cs  FPDF_FILEACCESS bridge → lazy FileStream reads (huge/Unicode paths)
    PdfiumException.cs     Maps FPDF_GetLastError to friendly messages

  Rune.Engine/          Document services (no UI dependency)
    PdfDocument.cs        Open/render/page-sizes/outline/links/properties (partial class)
    PdfDocument.Annotations.cs  AddMarkup/AddNote/AddInk/GetAnnotations/RemoveAnnotation/
                          Capture+RestoreAnnotation (undo)/SaveAs/IsDirty
    PdfDocument.Pages.cs  DeletePages/MovePages/ExportPages/InsertPages(FromFile)/RestoreMovedPages
    RenderScheduler.cs    THE single render thread + priority op queue (see §4)
    PageText.cs           Per-page text + char boxes → managed selection hit-testing
    PageLayout.cs         Immutable vertical-stack layout (zoom/rotation, min viewport w/h)
    Tiles.cs              TileKey + TileMath (MaxSingleTilePx = 1024 — see §7 gotcha)
    PageBitmap.cs         Pooled BGRA pixel buffer (ArrayPool)
    DipRect.cs            Simple rect struct in device-independent px
    OutlineItem.cs        TOC node model
    PdfLink.cs            Clickable-link model
    TextModels.cs         TextRect / TextSelection / SearchHit
    DocumentSearch.cs     Full-document text search (routes through the op queue)
    UndoStack.cs          Bounded per-document undo/redo stack (generic)
    BookmarkRemap.cs      Pure page-index remap math (delete/insert/move)
    AppState.cs           RecentFile(+Bookmarks)/SessionState/AppSettings/AppState/AppStateStore
                          (namespace Rune.Services — physically here so it's unit-testable)

  Rune.App/             WinUI 3 shell
    App.xaml(.cs)         Entry point; command-line file open; AppWindow icon;
                          merges Styles/Tokens.xaml + Styles/Controls.xaml
    MainWindow.xaml(.cs)  Shell: TabView-in-titlebar, SLIM header + hamburger menu,
                          floating zoom pill, find bar, presentation/shortcuts/bookmark/
                          undo wiring, settings/palette/update, drag-drop, homepage grid
    ShortcutCatalog.cs    Single source of truth for the F1 shortcuts overlay
    Styles/Tokens.xaml, Styles/Controls.xaml   spacing scale + shared control styles
    Controls/
      PdfViewer.xaml(.cs)     The viewport: Win2D canvas, virtualized scroll, zoom,
                              tiles, text selection, search, links, ink, night mode,
                              page-mutation refresh, annotation undo events
      DocumentView.xaml(.cs)  Per-tab: viewer + sidebar (thumbnails/chapters/bookmarks
                              switcher), page editing (reorder/delete/clipboard/insert),
                              undo stack owner, lazy open, save-in-place
      PresentationView.xaml(.cs) F5 fullscreen one-page-at-a-time overlay (tiled)
      CommandPalette.xaml(.cs) Ctrl+K fuzzy command palette
      BookmarkItem.cs, AnnotationEdit.cs, ThumbnailItem.cs, OutlineNode.cs, RecentCard.cs
    Services/
      PageClipboard.cs        App-wide page clipboard (serialized bytes, cross-tab)
      PrintService.cs         PrintManagerInterop + PrintDocument (live preview, page ranges)
      UpdateService.cs        GitHub-Releases self-update
      ThumbnailCache.cs       Homepage first-page thumbnails (disk-cached PNGs)
    Package.appxmanifest      MSIX identity + .pdf file-type association
    Assets/                   rune.ico + MSIX visual assets (generated)

tests/
  Rune.Tests/           xUnit — 93 tests against a generated corpus (see §6)

tools/
  gen-corpus.ps1        Hand-authors the test PDFs (no PDF lib needed)
  gen-icon.ps1          Draws the raido-rune icon + all MSIX assets
```

---

## 4. Architecture & rendering model (the important part)

```
WinUI shell (tabs in title bar, slim header + hamburger, floating zoom pill)
   └─ PdfViewer: Win2D CanvasVirtualControl inside a ScrollViewer
        └─ LRU tile cache (128 MB byte budget, ArrayPool buffers)
             └─ RenderScheduler: ONE dedicated thread
                  ├─ desired-tile list (visible > previews > prefetch)
                  └─ priority op queue (Interactive > tiles > Thumbnail > Background)
                       └─ thin P/Invoke → pdfium.dll
```

- **PDFium is NOT thread-safe.** ALL PDFium work is serialized through the
  single render thread (`RenderScheduler`), with the global lock
  (`PdfiumLibrary.Lock`) as a backstop. **Nothing calls PDFium on the UI
  thread anymore** — this was the v0.3 "random freeze" cause (v0.4 §9).
- **Two kinds of render-thread work, interleaved by priority:** the
  desired-tile list (reconciliation — see below) and one-off ops via
  `RunAsync(PdfWorkPriority, …)`. Loop order each pass: Interactive op → front
  desired tile → Thumbnail op → Background op. So selection/annotation edits
  outrank tile rendering, and tiles outrank sidebar thumbnails and search.
- **RenderScheduler uses desired-state reconciliation for tiles, not a queue.**
  The UI hands over the full prioritized "tiles I want right now" list
  (`SetDesired`), replacing the previous list. The loop always renders the
  front-most missing tile. Scrolling past something simply drops it from the
  next list — no stale work, no cancellation bookkeeping.
- **Text selection never touches PDFium on the pointer path.** Each visible
  page's text + per-char boxes are extracted once (`PageText`, via
  `FPDFText_GetCharBox`) and cached; hit-testing and range-rects are pure
  managed lookups. Desired-tile recompute is coalesced (50 ms) during scroll.
- **Progressive rendering:** each page draws white → stretched low-res preview
  (~216px, the "blurry-fast" pass) → crisp tiles at the exact current scale.
- **Tiles:** pages ≤ 1024px render as one bitmap; larger pages split into a
  1024px grid. Above the cap they're tiled.
- **Zoom** is native ScrollViewer `ZoomMode` (touch pinch, touchpad pinch,
  Ctrl+wheel all handled) folded into the real zoom on gesture-end via
  `RebaseZoom` — raster-scaled during the gesture, crisp after.
- **Coordinate spaces:** PDF page space is bottom-left origin; the app works in
  top-left "page points". `FPDF_PageToDevice` / `FPDF_DeviceToPage` convert
  (rotation-safe) — used for links, text, and all annotation geometry.
- **State/persistence:** JSON at `%LOCALAPPDATA%\Rune\state.json` (recents,
  session tabs+positions, settings, **per-document bookmarks**). Thumbnails
  cached at `%LOCALAPPDATA%\Rune\thumbnails\`. Migrates once from legacy `\Folio`.
- **UI is GNOME-Papers-proportioned** but native Windows (Mica + Fluent):
  one slim header row of flat icon buttons, everything else in a hamburger
  `MenuFlyout`; a floating zoom pill bottom-right. Spacing/typography come from
  `Styles/Tokens.xaml` + `Styles/Controls.xaml` (the only place new
  spacing/size constants live) — no per-control magic numbers.

---

## 5. Feature set (as shipped in v0.4.0)

- Tabs **in the title bar** (Chrome/Terminal style), lazy-loaded per tab
- Continuous virtualized scroll; zoom 10–640% at cursor; fit-width/page; rotate
- **Sidebar open by default** (Settings toggle) with a Papers-style bottom
  switcher: **thumbnails / chapters (TOC) / bookmarks**; internal & web links;
  back/forward
- **Full keyboard navigation** (always on): arrows scroll/page, PageUp/Down,
  Home/End, plus vim keys (Settings toggle)
- Text selection & copy; find-in-document with highlight-all + hit stepping
- **Annotations** (standard PDF annots via `FPDF_annot` + `FPDF_SaveAsCopy`):
  highlight / underline / strikeout from selection, sticky notes, and
  **freehand ink** (pen color/width in the hamburger). Right-click to delete.
  Save (Ctrl+S) / Save As (Ctrl+Shift+S). Dirty tab marker `•` + save prompt.
- **Page editing** in the thumbnail sidebar: multi-select, drag-to-reorder,
  Delete, **Ctrl+C/X/V page clipboard incl. across tabs**, drop an external
  `.pdf` into the sidebar to insert its pages. Serialized-bytes clipboard.
- **Undo / redo** (Ctrl+Z / Ctrl+Y): unified per-document stack over
  annotations (spec-based re-create) and page ops (snapshot / inverse-permute).
  Cleared on save-in-place + close. Dynamic menu labels.
- **User bookmarks** (Ctrl+B): named, per-document, persisted; sidebar pane
  with rename/delete/jump.
- **Presentation mode** (F5): fullscreen one-page-at-a-time, arrows/Space/click
  to advance, Esc/F5 to exit; lands the reader on the last shown page.
- **Keyboard shortcuts overlay** (F1 / Ctrl+?): GNOME-style two-column window,
  driven by `ShortcutCatalog` (single source of truth).
- **Night mode** (Ctrl+I): GPU `InvertEffect`, one cached effect per viewer
- **Command palette** (Ctrl+K): fuzzy filter + "Go to page N" + recents
- **Recent-docs homepage**: clean grid of aspect-correct thumbnail cards with
  theme-aware placeholders + empty state (thumbnails a Settings toggle)
- Session restore; printing with preview + page ranges; document properties
- **Self-update** from GitHub Releases (Settings toggle / "check now")

### Keyboard shortcuts (see `ShortcutCatalog.cs` for the authoritative list)
| Action | Keys |
|---|---|
| Open / close tab | `Ctrl+O` / `Ctrl+W` |
| Scroll / page up-down | `↑ ↓` / `PgUp PgDn`, `Space` `Shift+Space` |
| Previous / next page | `← / →` (vim: `p` / `n`) |
| First / last page | `Home` / `End` (vim: `gg` / `G`) |
| Back / forward | `Alt+←` / `Alt+→` |
| Find / next / prev | `Ctrl+F` / `F3` / `Shift+F3` |
| Command palette / shortcuts | `Ctrl+K` / `F1` (or `Ctrl+?`) |
| Zoom in/out/100%/fit page/fit width | `Ctrl++` / `Ctrl+-` / `Ctrl+1` / `Ctrl+0` / `Ctrl+2` |
| Night / sidebar / rotate | `Ctrl+I` / `F9` / `Ctrl+R` |
| Presentation / bookmark | `F5` / `Ctrl+B` |
| Highlight / pen / save / save as | `Ctrl+H` / `Ctrl+E` / `Ctrl+S` / `Ctrl+Shift+S` |
| Copy / cut / paste (text or pages) | `Ctrl+C` / `Ctrl+X` / `Ctrl+V` |
| Undo / redo | `Ctrl+Z` / `Ctrl+Y` |
| Print / properties | `Ctrl+P` / `Ctrl+D` |

Vim keys (`j k h l`, `gg`/`G`, `p`/`n`) are a Settings toggle. Page
copy/cut/paste applies when the thumbnail sidebar has focus; otherwise
`Ctrl+C` copies selected text.

---

## 6. Build / run / test (CLI only — **no Visual Studio installed**)

```powershell
# Build
dotnet build src/Rune.App/Rune.App.csproj -p:Platform=x64

# Run (accepts an optional PDF path; also --page N --zoom Z for scripted tests)
src/Rune.App/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/Rune.exe [file.pdf]

# Test (93 tests)
dotnet test tests/Rune.Tests/Rune.Tests.csproj

# Regenerate assets when needed
powershell -File tools/gen-corpus.ps1     # test PDFs → tests/corpus/
powershell -File tools/gen-icon.ps1       # icon + MSIX assets
```

**Test corpus** (`tests/corpus/`, generated): `hello.pdf` (2pp smoke),
`book-1000.pdf` (perf), `linked.pdf` (outline + internal/URI links),
`corrupt.pdf` (must throw, never crash). Tests cover interop/render, rotation
content, tile math, layout (incl. min-viewport-height), scheduler priorities +
cancellation, `PageText` selection parity with PDFium, outline, links,
text/search, `AppState` + bookmark persistence, `BookmarkRemap`, page editing
(delete/move/export/insert round-trips), and undo/redo (annotation spec
capture/restore, page snapshot restore, stack caps).

**Verifying UI features** is scripted, not just tested — drive the running
`Rune.exe` with `SetForegroundWindow`/`keybd_event` P/Invoke + `CopyFromScreen`,
then Read the PNG (see §7). The reusable helper used this session lives in the
session scratchpad (`shot.ps1` / `drive-rune.ps1`).

---

## 7. Environment gotchas (READ before debugging weird failures)

- **Smart App Control (SAC):** if you see `0x800711C7` ("Application Control
  policy has blocked this file") on run/`dotnet test`, SAC has flipped to
  **Enforce** and blocks unsigned locally-built binaries. Check
  `HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy` →
  `VerifiedAndReputablePolicyState` (0=off, 1=enforce, 2=eval). The user
  turned it **off**; the real fix is code signing. `dotnet build` still works
  under SAC (compile only); running/loading assemblies is what's blocked.
- **CanvasVirtualControl tile cap:** bitmaps wider than ~1.5k px silently fail
  to draw inside a drawing session on this hardware. `MaxSingleTilePx` is
  pinned to **1024** in `Tiles.cs` — do **not** raise it. (This was the root
  cause of the "rotate shows blank page" bug: only rotated landscape pages
  produced tiles that wide.)
- **No Visual Studio** — everything is `dotnet` CLI. Don't suggest VS-only flows.
- **Line endings:** commits warn `LF will be replaced by CRLF` (harmless);
  `.gitattributes` marks PDFs/images binary so autocrlf can't corrupt them.
- **UI automation for verification** (no computer-use MCP needed): drive Rune
  with `SetForegroundWindow` + `SetCursorPos` + `mouse_event`/`keybd_event`
  via `Add-Type` P/Invoke, screenshot with `CopyFromScreen`, then Read the
  PNG. Two rules: (1) drags need **relative `MOUSEEVENTF_MOVE` deltas**
  (SetCursorPos while a button is held delivers no move); (2) `SetProcessDpiAwareness(2)`
  and remember the display is **125% scale**. Caveat: if another app holds the
  foreground (e.g. a browser/video), input lands there and screenshots capture
  it — verify only when Rune can take focus.

---

## 8. Release process

Two artifacts per release; `gh` handles GitHub.

```powershell
# Portable zip (primary)
dotnet publish src/Rune.App/Rune.App.csproj -c Release -r win-x64 --self-contained `
  -p:Platform=x64 -p:WindowsPackageType=None
Compress-Archive <publish>\* artifacts/rune-vX.Y.Z-win-x64.zip

# Signed MSIX (for .pdf association / "default app")
dotnet build src/Rune.App/Rune.App.csproj -c Release -p:Platform=x64 `
  -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=D467A67B3240FD78931730C9E33B895191A5DA9A `
  -p:AppxPackageDir="..\..\artifacts\msix\"

# Publish
gh release create vX.Y.Z <zip> <msix> artifacts/rune-signing.cer --title "Rune vX.Y.Z" --notes-file notes.md
```

- **Version bump:** `<Version>` in `Rune.App.csproj` **and** `Version=` in
  `Package.appxmanifest`.
- **Signing cert:** self-signed `CN=Rune`, thumbprint
  `D467A67B3240FD78931730C9E33B895191A5DA9A`, expires 2027, in
  `Cert:\CurrentUser\My`; public `.cer` at `artifacts/rune-signing.cer`. Users
  must trust it (`Cert:\LocalMachine\Root`, admin) before `Add-AppxPackage`.
  **Installing the cert is a security action left to the user — don't automate it.**
- **CI:** `.github/workflows/ci.yml` runs build + test on `windows-latest`.
- **Always confirm with the user before anything goes public** (repo/release).
- Compliance: `LICENSE` (GPLv3) + `THIRD-PARTY-NOTICES.md` +
  `third_party/WindowsAppSDK-NOTICE.txt` ship inside every binary (PDFium's
  BSD/Apache terms require its license to accompany the DLL).

---

## 9. Version history

- **v0.1.0** — viewer core: tabs-in-titlebar, thumbnails/outline sidebar,
  links, text selection, search, night mode, print, command palette, session
  restore. (Built as milestones M0–M6.)
- **v0.2.0** (2026-07-14) — markup annotations (highlight/underline/strikeout)
  + sticky notes, Save/Save As, self-updater, pinch/Ctrl-wheel zoom. Also
  fixed three bugs: night mode (dead after the titlebar refactor), zoom
  gestures (never wired), rotate (blank pages from the tile-width bug).
- **v0.3.0** (2026-07-19) — freehand **ink** annotations; toolbar rebuilt as a
  stock **CommandBar** (Notepad pattern, hidden on start page); **recent-docs
  thumbnail homepage**; fixed the far-zoom-out **black box** (PageLayout
  min-viewport-height + vertical centering) and the stray **"Ctrl++" tooltip**
  (`KeyboardAcceleratorPlacementMode=Hidden`).
- **v0.4.0** (2026-07-20) — big smoothness + UX release.
  **Fixed:** the random freezes (all PDFium work moved off the UI thread onto
  the render thread's priority op queue; selection hit-tests now pure managed
  lookups via `PageText`; scroll recompute coalesced; night-mode effect
  cached); the "page stuck to the left on open" bug (fit deferred until the
  viewport is measured, no more 800×600 fallback). **Added:** always-on
  arrow/PageUp/Home-End navigation; a **GNOME-Papers-style redesign** (slim
  header + hamburger, floating zoom pill, redesigned sidebar with
  thumbnails/chapters/bookmarks switcher, clean recents grid, centralized
  tokens/styles); **presentation mode** (F5); **shortcuts overlay** (F1);
  **user bookmarks** (Ctrl+B); **page editing** (reorder/delete/clipboard/
  insert incl. cross-tab and external-PDF drop); **undo/redo** (Ctrl+Z/Y) over
  annotations + page ops. 50 → 93 tests.

---

## 10. Roadmap (not yet built)

- Form filling
- Digital signature verification
- Page **extract** to a new file (reorder/delete/insert already shipped in v0.4)
- More formats (ePub, CBZ — would need MuPDF; note AGPL implications)
- **Code signing** (Azure Trusted Signing ~$10/mo, or Microsoft Store) — the
  real fix for SAC/SmartScreen. **Deferred by user choice.**
- **winget** submission — **deferred by user choice** (winget does NOT bypass
  SAC; signing is the actual unblock).
- Smaller / size-optimized packages (current zip ~88 MB, self-contained runtime)

---

## 11. Standing conventions

- **Never publish (repo/release/anything outward-facing) without asking first.**
- Ship **unsigned** for now; document the SAC/SmartScreen limitation honestly.
- Verify features by **driving the real app** (screenshots), not just tests,
  for anything with a runtime surface — then commit.
- Commit messages end with `Co-Authored-By: Claude <...>`; branch off `main`
  only when the user asks to commit/push.
- Plan files from past sessions live in `C:\Users\djkho\.claude\plans\`.
