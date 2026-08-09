# Rune — Project Reference

> A single-file brain-dump so a fresh session (human or AI) can understand
> and continue this project without re-deriving context. Last updated for
> **v0.6.0** (2026-08-09): interaction while rotated, page extract, typed
> signatures, signature resize, PDFium 153, and 19 MB off the package.
>
> **Start here:** §1 what it is · §4 how rendering works (the load-bearing part)
> · §7 gotchas (read before debugging) · §10 known bugs · §13 current state.

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

- **GitHub:** https://github.com/DanialJaved/rune (public; `gh` CLI is
  authenticated as `DanialJaved`)
- **Owner/dev:** Danial Javed — new to C#/.NET, Rust, and web stacks; explain
  non-obvious .NET concepts (P/Invoke, async, XAML binding) while building.

---

## 2. Tech stack (decisions are settled — don't re-litigate)

| Layer | Choice | Notes |
|---|---|---|
| Language/runtime | **C# / .NET 10** | |
| UI | **WinUI 3** (Windows App SDK **2.2.0**, referenced as sub-packages — see §7) | Fluent, Mica, dark mode |
| PDF engine | **PDFium** via `bblanchon.PDFium.Win32` NuGet (153.x) | Chrome's renderer; BSD-3-Clause/Apache-2.0 |
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
                          undo wiring, settings/palette, drag-drop, homepage grid
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
      DialogHost.cs           Serializes every ContentDialog (WinUI allows ONE — see §7)
      PageClipboard.cs        App-wide page clipboard (serialized bytes, cross-tab)
      PrintService.cs         PrintManagerInterop + PrintDocument (live preview, page ranges)
      ThumbnailCache.cs       Homepage first-page thumbnails (disk-cached PNGs)
    Package.appxmanifest      Store identity + .pdf file-type association (§8b)
    Assets/                   rune.ico + MSIX visual assets (generated)

tests/
  Rune.Tests/           xUnit — 312 tests against a generated corpus (see §6)

tools/
  gen-corpus.ps1        Hand-authors the test PDFs (no PDF lib needed)
  gen-icon.ps1          Draws the raido-rune icon + all MSIX assets

docs/
  store-listing.md      Store submission copy: description, search terms, age
                        rating answers, runFullTrust justification, screenshot plan
  store-screenshots/    6 × 1920×1080 PNGs for the Store listing
PRIVACY.md              Required by the Store (live URL is checked at cert time)
```

Engine files added in v0.4.x worth knowing about: `ErrorLog.cs` (crash log,
never throws), `ViewRotationMath.cs` (negative quarter-turn normalization),
`ThumbnailMetrics.cs` (aspect-correct box sizing), `BookmarkRemap.cs`,
`UndoStack.cs`, `PageText.cs`.

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

## 5. Feature set (as shipped in v0.6.0)

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
  **freehand ink** (colour/width panel on the pen button itself). Right-click to delete.
  Save (Ctrl+S) / Save As (Ctrl+Shift+S). Dirty tab marker `•` + save prompt.
- **Page editing** in the thumbnail sidebar: multi-select, drag-to-reorder,
  Delete, **Ctrl+C/X/V page clipboard incl. across tabs**, drop an external
  `.pdf` into the sidebar to insert its pages. Serialized-bytes clipboard.
  **Extract** the selection to a new file (context menu + palette), which leaves
  the open document untouched.
- **Everything interactive works while the view is rotated** (v0.6.0) —
  selection, markup, links, form filling, signing. `PageRotationTransform` maps
  between unrotated page space and the drawn box; find results and selection now
  survive a Ctrl+R rather than being cleared.
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
- **Form filling** (AcroForm text/checkbox/radio/combo/list): PDFium's form-fill
  environment drives every edit through `FORM_OnChar` — there is no programmatic
  setter — with Rune-drawn field borders over the top. Values round-trip through
  save.
- **Signing**: draw a signature, **type it** in one of Windows' handwriting faces
  (`SignatureFonts`, nothing bundled), or **import a photo or scan and have the
  paper keyed out automatically** (`SignatureMatte`). Placed as a stamp annotation
  with a live semi-transparent preview under the cursor, wheel-sizing before
  placement, and drag-to-move **or aspect-locked corner-handle resize** after.
  Saved signatures are reusable and stay on the device.
- **Signature details**: reports what a signed document *claims*, including
  whole-file coverage. Deliberately does **not** verify — see the disclaimer in
  `MainWindow.xaml.cs`, which must never be softened.
- **Flatten** (`PdfDocument.Flatten`): bakes annotations and form values into
  page content for a fixed, non-editable copy.
- Session restore; printing with preview + page ranges; document properties

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
| Night / sidebar | `Ctrl+I` / `F9` |
| Rotate right / left | `Ctrl+R` / `Ctrl+Shift+R` |
| Presentation / bookmark | `F5` / `Ctrl+B` |
| Highlight / pen / save / save as | `Ctrl+H` / `Ctrl+E` / `Ctrl+S` / `Ctrl+Shift+S` |
| Pen, highlighter, note, sign, eraser | annotation toolbar (no direct chords) |
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

# Test (312 tests)
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
- **Do not add `Microsoft.WindowsAppSDK` back as a package reference.**
  `Rune.App.csproj` deliberately references the six sub-packages it needs
  (Base, Foundation, InteractiveExperiences, WinUI, DWrite, Runtime) instead.
  The meta-package hard-depends on `.Widgets`, `.AI` and `.ML`, and `.ML` pulls
  in `Microsoft.Windows.AI.MachineLearning` — `onnxruntime.dll` (20.7 MB) plus
  `DirectML.dll` (17.8 MB) in a PDF reader that runs no inference. There is no
  supported opt-out property; the sub-package list is the only route, and none
  of the six depends on AI, ML or Widgets. **Upgrading the SDK means bumping six
  lines and re-reading the meta-package's nuspec** for any newly added
  dependency Rune actually needs. Verify by *launching* the build, not just
  compiling: a missing WinAppSDK binary fails at first XAML load. Night mode is
  the sharpest single check — it goes through Win2D's `InvertEffect`.
- **PDFium form-fill landmines** (all cost real time in v0.5.0; see
  `PdfiumFormEnvironment.cs` and `PdfDocument.Forms.cs`):
  - **`FPDFAnnot_SetFormFieldValue` does not exist.** The only way to change a
    field's value is to drive the event API — click, then `FORM_OnChar`. Don't
    go looking for a programmatic setter; there isn't one.
  - `FPDF_FORMFILLINFO.version` must be **1**. Version 2 appends XFA members and
    this build has no XFA, so declaring 2 makes PDFium read past the struct.
  - PDFium stores the **pointer** to `FPDF_FORMFILLINFO`, so it lives in
    `AllocHGlobal` memory, not on the managed heap where the GC can move it —
    same rule as `FileAccessAdapter`.
  - `FFI_GetLocalTime` is left NULL deliberately: it returns a 16-byte struct
    **by value**, and it's only used by form JS, which this build can't run.
  - `FFI_SetTimer`/`FFI_KillTimer` are left NULL deliberately — they exist for
    caret blink, which would re-rasterize the focused field's tiles twice a
    second. A decision, not an oversight.
  - `FFI_GetPage` must **not** call `FORM_OnAfterLoadPage`, and must not load a
    page — PDFium calls it from inside its own page setup. It returns only pages
    the cache already holds.
  - **Never `RunAsync` from inside a form callback** — instant deadlock.
  - **`FPDF_SetFormFieldHighlightColor` takes BGR, not RGB**, despite the header
    saying `0xxxrrggbb`. Passing `0x3399FF` renders peach. Verified on screen.
  - **Kill form focus before every save.** PDFium holds the in-progress value in
    the focused widget, so saving with focus alive writes the field's *previous*
    value. `SaveAs` and `FlattenPage` both do this themselves.
  - `FPDFImageObj_SetBitmap` takes a page **array**, not a page.
  - `FPDFSignatureObj_GetSubFilter`/`GetTime` are ASCII; `GetReason` is UTF-16.
- **Theme brushes in code-behind**: never resolve one via
  `Application.Current.Resources["...Brush"]` — it returns the **dark** value
  whatever the active theme is. Define a `Style` in `Styles/Controls.xaml` and
  assign `element.Style`; a Style's setters resolve against the element's real
  theme. Reading a *Style* out of `Application.Current.Resources` is fine.
- **`ContentDialog` doesn't follow `Window.Content`'s theme either.** It is
  hosted in a popup outside the content tree, so it tracks the OS: choosing
  Light in Rune on a dark-mode Windows gave a dark dialog over a light app.
  `ShowDialogAsync` now sets `dialog.RequestedTheme` centrally — every dialog in
  the window goes through it, so don't call `ShowAsync` directly.
- **`InfoBar` stretches to its container.** Its template is a full-width bar, so
  dropping one into the row-2 overlay Grid painted it straight across the
  sidebar with its message clipped. Notices go through `NoticeHost`, which
  bounds it with `MaxWidth` + centre alignment. There are two hosts: one per
  `DocumentView` (inside `SplitView.Content`, so it can never reach the sidebar
  and re-centres itself when the pane toggles — no width arithmetic), and one at
  window level for messages with no document open. `MainWindow.ShowNotice`
  routes between them; `ShowError` is a thin shim over it.
- **Caption buttons don't follow `Window.Content`'s theme.** Because the theme is
  set on the content root rather than `Application.RequestedTheme`, the system
  caption glyphs track the OS. `MainWindow.ApplyThemeToChrome` sets
  `AppWindow.TitleBar.Button*Color` explicitly — and their backgrounds must stay
  `Transparent` or Mica dies behind the tab strip.
- **`ChangeView` clamps against the OLD extent.** After `RebuildLayout()` assigns
  `Canvas.Width/Height`, the ScrollViewer has not re-measured, so `ChangeView`
  clamps the requested offset to the previous `ScrollableWidth/Height` —
  zooming in silently lands short. Call `Scroller.UpdateLayout()` in between
  (see `ScrollToAnchor`). Same class as the stale-`ViewportWidth` note above.
  `ChangeView` also returns `bool` and does nothing when it returns false.
- **Zoom must anchor in PAGE space, never document space.** `PageLayout` is
  affine, not a pure scale: `Margin` and `PageGap` are constant DIPs and pages
  are centred. Scaling a scroll offset by the zoom ratio therefore mis-scales
  those constants, and since the gap is added once per page the error grows
  with page index — barely visible on page 1, tens of DIPs by page 40. Use
  `ZoomAnchor.Capture`/`Restore`; `ZoomAnchorTests` pins the behaviour.
- **Handle Ctrl+wheel on the Canvas, not the ScrollViewer.** Attaching to the
  ScrollViewer needs `handledEventsToo: true`, which means the ScrollViewer has
  *already* applied its own Ctrl+wheel zoom — so the real zoom stepped and its
  `ZoomFactor` got folded in on top, zooming roughly twice as far per notch.
  `PointerWheelChanged` bubbles from the hit-test target, so handling it on the
  child pre-empts the ScrollViewer entirely.
- **Win2D controls don't work inside a `ContentDialog`.** A `CanvasControl`
  hosted in the dialog's popup never gets a device and silently renders
  nothing — the signature pad draws with XAML `Polyline`s and uses Win2D only
  offscreen (`CanvasRenderTarget` + `CanvasDevice.GetSharedDevice()`), which is
  unaffected. `InkCanvas` is not available in this SDK at all.
- **Win2D renders premultiplied; PDFium composites straight alpha.** Pinned by
  `StampTests.HalfAlphaGrey_CompositesAsStraightAlpha` — a mid-grey at 50% must
  land at ~191 over white, not ~255. `SignaturePad.ToStraightAlpha` is the one
  place the conversion happens; a fully transparent or fully opaque pixel is
  identical either way, which is why the earlier transparency test couldn't
  detect the difference.
- **`BitmapTransform.ScaledWidth` is in STORED space; the buffer arrives in
  ORIENTED space.** Measured against a 1600x1200 JPEG carrying EXIF orientation
  6 (so it displays 1200x1600): asking for `ScaledWidth=1024, ScaledHeight=768`
  alongside `ExifOrientationMode.RespectExifOrientation` returns an upright
  **768x1024** buffer. So scale off `decoder.PixelWidth/PixelHeight`, then
  transpose the *requested* numbers to describe the result — never scale
  `OrientedPixelWidth/Height` separately, and never report `PixelWidth` as the
  buffer's width (that was a real bug: a portrait phone photo stamped as a
  diagonal smear). Both orderings yield byte-identical buffer *lengths*, so no
  assertion can catch getting this wrong; only looking at the pixels can.
  `SignatureStore.DecodeAsync` is the one place this is handled.
- **`BitmapDecoder.CreateAsync` throws a bare `COMException` on a bad file** —
  not `ArgumentException`. A renamed `.pdf` reaches it as a WIC HRESULT, and
  since the import handler is `async void`, a filtered catch there takes the
  whole process down. `SignatureStore` catches everything and logs.
- **Direct2D refuses straight-alpha bitmaps.** `CanvasBitmap.CreateFromBytes(...,
  CanvasAlphaMode.Straight)` throws `COMException 0x88982F80`
  (`WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT`) — D2D only draws PREMULTIPLIED (or
  ignored) alpha. This shipped in the signature hover preview and made it draw
  **nothing at all**: the throw escaped `DrawSignatureGhost` and took the dashed
  outline down with it, so the whole preview vanished instead of degrading. Rune
  holds signature pixels as straight alpha because that is what PDFium
  composites, so anything handing them to Win2D must go through
  `SignatureMatte.ToPremultiplied` first. A draw path that builds a bitmap
  should also keep that build in its own try/catch, so a bad buffer costs the
  bitmap and not the rest of the frame.
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
- **Clean screenshots** (no taskbar/desktop bleed): size the window to exactly
  1920×1080 at (0,0) with `SetWindowPos`, then capture with
  `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)` — that grabs the window's own
  pixels, so nothing on top can intrude. In-app `Flyout`s (e.g. the pen panel)
  *are* captured; a `MenuFlyout` shown via `ShowAt(element, point)` may render in
  its own HWND and **not** appear — screen-capture those instead.
  **It returns a solid black bitmap if called before the window has finished its
  first composition** — wait for the app to actually draw rather than shooting a
  second after launch. Black output means "too early", not "unsupported"; that
  misreading cost a session's worth of confusion. `CopyFromScreen` is the
  fallback for flyouts, but it cannot produce a clean 1920×1080 on a 1080-tall
  display, because the taskbar overlays the bottom edge even for a topmost window.
- **`ScrollViewer.ViewportWidth` is STALE inside `SizeChanged`.** A ScrollViewer
  refreshes it during its own arrange pass, which runs *after* the event. Reading
  it there lays the document out against the previous size. Use
  `SizeChangedEventArgs.NewSize`. (This was the sidebar-toggle bug: page pinned
  left with ghost strips of the old render.)
- **Never read theme brushes via `Application.Current.Resources["..."]` in code.**
  It returns the **dark-theme** value regardless of the active theme, so e.g.
  `TextFillColorPrimaryBrush` rendered white-on-white in light mode. Use
  `{ThemeResource}` in XAML, or build an explicit `SolidColorBrush`.
- **`ToggleButton.IsChecked` is `bool?`; `ToggleMenuFlyoutItem.IsChecked` is `bool`.**
  Chained assignment across the two won't compile — assign separately.
- **`RuntimeIdentifier` must follow `$(Platform)`.** Pinning it to `win-x64`
  breaks the ARM64 leg of a multi-arch Store bundle (`NETSDK1032`); both RIDs
  must also be in `RuntimeIdentifiers` so restore can resolve them.
- **Symbol packages need `mspdbcmf.exe`** from the VS C++ workload (not installed
  here) — `AppxSymbolPackageEnabled=false`. They're optional for the Store.
- **PowerShell + `git commit -m "..."`**: quotes/apostrophes inside the message
  break argument parsing and scatter the body across `pathspec` errors. Write the
  message to a file and use `git commit -F <file>`.
- **Editing files containing private-use glyph chars** (e.g. `FontIcon Glyph=""`
  in `MainWindow.xaml.cs`): exact-string edits spanning those lines fail to match.
  Edit around them, or splice by line range.
- **Focus traps** (see §10 known bugs): the Win2D canvas is not focusable, so
  clicking the page never returns focus. If focus sits on the tab strip or the
  page-number box, navigation keys are dead. When scripting, navigate via the
  command palette (Ctrl+K → type a number → Enter) rather than PageDown.

---

## 8. Release process

**The Microsoft Store is the install path Rune promotes** (§8b). GitHub Releases
carry **one artifact — the portable zip** — for people who want Rune without the
Store. As of v0.5.0 the sideloaded MSIX and its self-signed certificate are no
longer published: the cert obliged every user to run an admin PowerShell command
to trust a certificate, which is a worse security ask than the Store's
Microsoft-signed package solves for free.

```powershell
# Portable zip — the only GitHub artifact
dotnet publish src/Rune.App/Rune.App.csproj -c Release -r win-x64 --self-contained `
  -p:Platform=x64 -p:WindowsPackageType=None
Compress-Archive <publish>\* artifacts/rune-vX.Y.Z-win-x64.zip

gh release create vX.Y.Z <zip> --title "Rune vX.Y.Z" --notes-file notes.md
```

- **Version bump:** `<Version>` in `Rune.App.csproj` **and** `Version=` in
  `Package.appxmanifest`.
- The portable zip is **unsigned**, so SAC/SmartScreen apply to it — documented
  honestly in the README. Signing it is still open (§11).
- **CI:** `.github/workflows/ci.yml` runs build + test on `windows-latest`, with
  `permissions: contents: read`.
- **Always confirm with the user before anything goes public** (repo/release).
- Compliance: `LICENSE` (GPLv3) + `THIRD-PARTY-NOTICES.md` +
  `third_party/WindowsAppSDK-NOTICE.txt` ship inside every binary (PDFium's
  BSD/Apache terms require its license to accompany the DLL).

---

## 8b. Microsoft Store

Listed as **"Rune PDF Reader"** ("Rune" alone was taken — and is poor Store SEO
anyway; nobody searching "rune" wants a PDF reader). The package identity, exe,
repo and icon all stay `Rune`; only the display name differs.

| Field | Value |
|---|---|
| Identity/Name | `Danimite.RunePDFReader` |
| Identity/Publisher | `CN=513DE1BC-C862-44F8-AEAD-F60E359F4BBF` |
| PublisherDisplayName | `Danimite` |
| Partner Center | developer name **Danimite** (sign in with the account that reserved the name) |

These must match Partner Center **exactly** or the upload is rejected. The
bundle is uploaded unsigned — the Store re-signs it, which is why Store installs
get no SmartScreen/SAC warning.

**Product ID `9NH37840QDM6`** — live at https://apps.microsoft.com/detail/9NH37840QDM6

**winget works, and needed no work.** The listing is mapped into the `msstore`
source, so `winget install --id 9NH37840QDM6 --source msstore --exact` installs
the same Microsoft-signed package. Verified with `winget show --id 9NH37840QDM6
--source msstore` (v0.6.0 prep). The community `winget-pkgs` route was never
viable: it wants a downloadable installer URL, which Store-only distribution
doesn't provide.

`winget show` also reads back the **live** Store description, which makes it a
free way to check that what Partner Center actually serves matches
`docs/store-listing.md`.

```powershell
# Store upload bundle (unsigned — the Store signs it), x64 + ARM64
dotnet restore src/Rune.App/Rune.App.csproj
dotnet build src/Rune.App/Rune.App.csproj -c Release -p:Platform=x64 `
  -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=false `
  -p:AppxBundle=Always -p:AppxBundlePlatforms="x64|arm64" `
  -p:UapAppxPackageBuildMode=StoreUpload `
  -p:AppxPackageDir="..\..\artifacts\store\"
# → artifacts/store/Rune.App_X.Y.Z.0_x64_arm64_bundle.msixupload  (~106 MB)
```

- **Rune contains no networking code, and must not gain any.** `UpdateService`
  was deleted in v0.5.0. That is what lets the privacy declaration and the
  `runFullTrust` justification both say "makes no network connections, collects
  no data" *unconditionally* — previously the claim held only because the
  updater was gated off for packaged builds. Adding a network call back
  invalidates both, and pointing a Store user at a download outside the Store is
  a certification failure on its own.
- `runFullTrust` is flagged at submission as a restricted capability needing
  approval. Expected for every WinUI 3 desktop app; justification text is in
  `docs/store-listing.md`.
- `PRIVACY.md` must be live on `main` before submitting — certification follows
  the URL in the listing.
- Store screenshots must use a **licence-safe** document (they're published
  commercially). The current set uses the NASA Systems Engineering Handbook —
  a US Government work, so public domain. **Never** shoot the user's own files;
  filenames leak in the tab strip and recents list.

---

## 9. Version history

- **v0.6.0** (2026-08-09) — the release that stops the reader disabling itself.
  **Fixed: rotating the page no longer turns half the app off.** Text selection,
  annotation, links, form filling and the whole signature flow all early-returned
  on `_rotation != 0` — fourteen guards across five files, with nothing on screen
  to say why. The cause was two helpers: `ToPageLocal` undid the centring offset
  and the zoom, which lands in the *drawn* box, while every consumer (`PageText`
  char boxes, form field rects, annotation rects, the x/y `AddStamp` takes) is
  measured in unrotated page points; `HighlightRect` had the inverse assumption.
  On a quarter turn those two spaces have the page's axes swapped, so rather than
  return a point on the wrong part of the page, each caller switched itself off.
  New `PageRotationTransform` maps between them and the guards are gone. It is
  managed arithmetic rather than `FPDF_DeviceToPage` on purpose — pointer moves
  hit it per event, and v0.4.0 moved hit-testing off PDFium precisely to stop that
  freezing the UI thread. Two paths needed more than the guard removed: form
  field borders now go through the same rotation PDFium's own widget fill gets,
  and a signature placed while rotated has its pixels turned the other way first
  (`SignatureMatte.RotateQuarterTurns`) so it reads upright against the content
  the user was looking at rather than against the file's axes.
  `Rotate()` also stopped discarding the user's work: it cleared selection,
  search hits, links and the page-text cache on every turn because none of it
  could be placed once rotated. All four are in unrotated page coordinates and
  therefore rotation-independent, so find a hit, rotate, and the hit is still
  there. Tiles and previews are still dropped — those really are per-rotation,
  and that invalidation is the v0.2 blank-page fix.
  **Added: page extract.** `ExportPages` already returned a selection as PDF
  bytes (it backs the page clipboard), so extract needed no engine work: a
  thumbnail context-menu entry, a palette entry, and a save picker. Extracting
  over the open document is refused rather than pulling the file out from under
  the read handle.
  **Added: resizing a placed signature**, with aspect-locked corner handles.
  This was attempted, backed out, and then solved from the other end, which is
  worth recording. Editing the appearance in place —
  `FPDFPageObj_SetMatrix` plus `FPDFAnnot_UpdateObject` — is exact once and then
  compounds: `UpdateObject` re-serializes the appearance while keeping the old
  `/BBox`, PDFium maps that BBox onto the annotation rect, and so growing a stamp
  and shrinking it back drew it at half the size asked for, with `GetMatrix`
  reading back correct all the while. Clearing the appearance first to reset the
  BBox destroys its objects. What broke the deadlock was disproving the premise
  in the old roadmap note: PDFium *will* hand a stamp's pixels back, through
  `FPDFImageObj_GetRenderedBitmap`, straight-alpha and intact after a save and
  reopen. So `ResizeStamp` reads the pixels, removes the annotation and re-creates
  it through `AddStamp`, which builds a fresh appearance every time and therefore
  cannot accumulate. It also works on a signature that was already in the file,
  which a session-side pixel cache never would have.
  **Added: typed signatures**, the third input mode beside draw and import. No
  font is bundled — Segoe Script, Segoe Print and Ink Free ship with Windows, and
  bundling one would undo part of the size work below. `SignatureFonts` resolves
  each style against the installed set and feeds the same answer to the preview
  and the render, so they cannot disagree; when nothing resolves the pad disables
  the field and says why instead of quietly producing something that looks typed.
  Cropping uses DirectWrite's `DrawBounds` rather than the layout box, because a
  script face overhangs its box on both sides.
  **Removed 19 MB from the zip (88 → 69 MB).** The `Microsoft.WindowsAppSDK`
  meta-package hard-depends on `.Widgets`, `.AI` and `.ML`, and `.ML` pulls in
  `Microsoft.Windows.AI.MachineLearning`: `onnxruntime.dll` (20.7 MB) plus
  `DirectML.dll` (17.8 MB) inside a PDF reader that runs no inference. There is no
  opt-out property, so `Rune.App.csproj` now references the six sub-packages Rune
  actually needs. Size was the smaller half of the reason: an OAuth component and
  an ML runtime sitting in a package whose Store listing and `runFullTrust`
  justification both say "no network connections, collects no data" is a fair
  question, and "the meta-package put it there" is not much of an answer. See §7
  before touching it again.
  **Security:** PDFium 152.0.7961 → **153.0.7988**, a full Chrome milestone of PDF
  fixes. `SECURITY.md` added, pointing exploitable bugs at private vulnerability
  reporting and drawing the line between a PDFium parser bug (Chromium's tracker)
  and Rune's own code.
  **Added: "Report a problem…"** in the menu — version, Store or portable,
  Windows build, and where the log is. `ErrorLog` had been writing to
  `%LOCALAPPDATA%\Rune` from eleven call sites since v0.4.1 with nothing in the
  app saying so, and with no telemetry by design that is the only route a crash
  reaches anyone. Issue templates ask for the same details up front.
  **Also:** winget turned out to already work through the `msstore` source, so it
  needed a README line and not a project; `MaxVersionTested` was two Windows
  builds behind. 244 → 312 tests.
- **v0.5.1** (2026-08-08) — signature import that actually works on a photo.
  **Added:** `SignatureMatte`, an adaptive local matte that keys the paper out
  of a photographed or scanned signature automatically — tiled 90th-percentile
  paper estimate with ink-tile refill, a data-driven ink level (which is what
  removes the need for a sensitivity slider), alpha unmixing so soft edges stay
  faithful, and an alpha-sum crop. One "Remove background" checkbox, on by
  default, auto-unticked for a source that already has transparency. Imports are
  downscaled at decode time to `TileMath.MaxSingleTilePx`.
  **Fixed:** three real bugs on that path — `BitmapDecoder.CreateAsync` throws a
  bare `COMException` for an unreadable file and, through an `async void`
  handler, killed the process; `SignatureStore` reported `PixelWidth` for an
  EXIF-rotated photo, which stamped a portrait phone shot as a diagonal smear;
  and full-resolution buffers silently broke the on-page hover ghost.
  **Also fixed:** the hover ghost had *never* rendered — it asked Win2D for
  `CanvasAlphaMode.Straight`, which Direct2D rejects outright
  (`WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT`), and the throw took the dashed outline
  down with it so the preview vanished instead of degrading. 209 → 244 tests.
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
- **v0.4.1** (2026-07-29) — bug-fix release from user testing of v0.4.0.
  **Fixed:** the **crash on "check for updates" with a document open**. There
  was no `Application.UnhandledException` handler anywhere, so a second
  concurrent `ContentDialog` (WinUI allows one) threw out of an `async void`
  and killed the process. Two paths produced it: the Settings "check now"
  button lives *inside* the Settings dialog, and the startup check could
  collide with a user-initiated one — the latter is why a document had to be
  open (restoring one delays the startup check into collision range). Added
  `ErrorLog` + app-level `UnhandledException`/`UnobservedTaskException`
  handlers, `DialogHost` (serializes all 10 dialog sites), a single-flight
  update guard, and a never-throws contract on `DownloadAndApplyAsync`.
  Self-update no longer discards unsaved annotations (it prompts *before*
  downloading), and the releases-page fallback no longer opens the browser
  when you click Cancel.
  **Fixed:** thumbnails **not matching page shape** — a fixed-width box with
  only a `MinHeight` letterboxed every landscape/4:3/16:9 page, invisibly
  (hardcoded white bars behind white slides). Boxes now take each page's own
  aspect ratio, sized *before* the render arrives (so the list no longer
  reflows mid-scroll), follow Ctrl+R, and are theme-aware; homepage cards get
  a bordered, correctly-shaped page box.
  **Added:** pen colour/width panel **on the pen button** (clicking keeps
  drawing on and re-opens it; Esc / Ctrl+E / "Stop drawing" exit), and
  **fit-width / fit-page / rotate-left / rotate-right always visible in the
  header** (+ Ctrl+Shift+R for rotate-left). 93 → 118 tests. Also: `main` is
  now branch-protected (no force-push, no deletion).
- **v0.5.0** (2026-07-31) — the release that turns a reader into a document tool.
  **Added: interactive form filling.** The whole of `fpdf_formfill.h` was
  unbound; v0.5.0 adds the form-fill environment (`PdfiumFormEnvironment`), the
  `FPDF_FFLDraw` pass inside `RenderRegionToBuffer` (so filled fields show in
  tiles, thumbnails, presentation and print alike), pointer/keyboard routing,
  and XFA detection with an honest info bar instead of a dead form.
  **Added: flatten** (`FPDFPage_Flatten`) and **signature details** — read-only,
  reporting only what the file claims plus `/ByteRange` coverage, and never
  asserting validity, because Rune ships no cryptography.
  **New architecture: the page-handle cache** (`PdfDocument.PageCache.cs`) —
  PDFium needs a stable `FPDF_PAGE` across keystrokes, so pages are no longer
  loaded and closed per operation. Every PDFium call site was moved onto it, and
  the last seven off-render-thread call sites (including `PrintService`, which
  rendered on the *UI* thread) now go through the work queue.
  **Fixed** all three v0.4.1 known bugs (see §10), plus the two
  `Application.Current.Resources` brush reads and caption buttons that ignored
  the app's theme. Colour now lives in `Styles/RuneColors.cs` (Win2D) and
  `{ThemeResource}` brushes (XAML); ~20 hardcoded `Opacity` values became real
  secondary/tertiary text brushes.
  **New logo**: the raido rune set on a document page, authored as
  `assets/rune.svg` and rasterized by `tools/gen-icon.ps1` through WPF — plus
  the full scale-100..400 asset set the Store recommends, where before there was
  only scale-200.
  **Fixed two long-standing viewer bugs** found by using the build:
  *Zoom didn't follow the cursor* — both zoom paths scaled the scroll offset by
  the zoom ratio, which assumes document space scales purely with zoom. It
  doesn't (constant `Margin`/`PageGap`, centred pages), and the error compounded
  once per page gap: negligible on page 1, tens of DIPs by page 40. Anchoring
  now goes through `ZoomAnchor` in page space. Ctrl+wheel is also handled
  directly on the Canvas instead of by the ScrollViewer, so it anchors exactly
  and never raster-scales.
  *The page went blurry and stayed that way* — `UpdateDesiredTiles` returned
  early whenever `ScrollViewer.ZoomFactor != 1`, and only a settling
  `ViewChanged` ever reset it. A missed one left the tile pipeline permanently
  unable to request a crisp tile. It now folds the factor in itself rather than
  giving up. Also fixed a stale-key leak in the v0.5.0 form-refresh sets that
  made evicted tiles get re-requested forever.
  **Rebuilt the notice surface.** The signature/XFA notice was a raw `InfoBar`
  in the window overlay, so it stretched across the sidebar with its text
  clipped, covered the find bar, and reopened itself after every page edit
  because its close button recorded nothing. It is now `NoticeHost` — a compact
  floating card bounded to the document area — and it is the app's *only*
  message channel, replacing the error bar that had the same stretch bug and
  was announcing successful flattens in red with error semantics. Dialogs also
  now inherit the app theme instead of the OS's. 118 → 197 tests.
  **Pre-submission security pass** (same release, before the Store upload):
  PDFium bumped to **152.0.7961** — it parses untrusted input, so its releases
  carry Chrome's PDF fixes and a stale pin is the largest avoidable risk in a
  submission. **`UpdateService` deleted**: with distribution moving to the Store,
  the self-updater had nothing to update from, and removing it took with it the
  app's only network code *and* a download path that verified neither hash nor
  signature. Rune now makes no network requests in any build, which is what turns
  the privacy declaration and the `runFullTrust` justification from conditional
  claims into unconditional ones. Whole-page renders are clamped to a pixel
  budget: `RenderRegion` computed `width × 4 × height` in `int`, and a page may
  declare a MediaBox up to the PDF maximum of 14400 pt, which overflows to a
  negative at print resolution and threw out of `ArrayPool.Rent`. Tiles were
  never exposed (capped at 1024 px); print and thumbnails were. 197 → 209 tests.

---

## 10. Known bugs

None open. The three v0.4.1 bugs below, and the two zoom/blur bugs found while
using v0.5.0 (see §9), were all fixed in v0.5.0; kept here with their fixes
because each one's cause is worth remembering.

1. ~~Navigation keys go dead after clicking a tab or the page-number box.~~
   **Fixed.** `PdfViewer` is now `IsTabStop = true` and takes focus on pointer
   press (it had to be, for form fields to receive keystrokes at all).
   `IsTextInputFocused()` additionally reports true while a PDF form field has
   focus, so arrows move the caret rather than scrolling the document.
2. ~~Selected pages are nearly invisible in light theme.~~ **Fixed.** Cause was
   the thumbnail `Border`'s opaque background painting over the ListViewItem's
   selection tint. The current page now draws its own accent ring as a second
   Border overlay (`ThumbnailItem.RingThickness`), owing nothing to the
   container. Only the *thickness* is bound — the accent brush stays in XAML as
   a `{ThemeResource}`.
3. ~~Night mode doesn't invert sidebar thumbnails.~~ **Fixed.** `DocumentView.ToBitmap`
   inverts BGRA during the copy it already performs (~55k pixels, sub-ms), and
   `PdfViewer.NightModeChanged` re-renders realized containers. The homepage
   recent-document cards are deliberately **not** inverted: the start page isn't
   a document, and night mode is a per-document reading mode.

---

## 11. Roadmap (not yet built)

- **Form JavaScript** — needs a V8-enabled PDFium build (`bblanchon.PDFium.V8.*`,
  a one-line csproj swap for a much larger binary). Without it, auto-calculating
  fields accept typed values but never recalculate.
- **Signature validation** — out of reach without a crypto stack. Rune reports
  only what the file claims plus byte-range coverage, and must never say "valid".
- More formats (ePub, CBZ — would need MuPDF; note AGPL implications)
- **Code signing** — *solved for Store installs* (the Store re-signs). Still open
  for the portable zip: Azure Trusted Signing ~$10/mo. Deferred.
- Smaller packages still. v0.6.0 took the zip from ~88 MB to **69 MB** by
  dropping the Windows App SDK's AI, ML and Widgets payload (see §7); what
  remains is the self-contained .NET + WinUI runtime, and trimming that is a
  much harder problem — `PublishTrimmed` and XAML's reflection do not get along.

---

## 12. Standing conventions

- **Never publish (repo/release/anything outward-facing) without asking first.**
- **No AI attribution in commits, PR bodies, or release notes.** History was
  rewritten on 2026-07-29 to strip `Co-Authored-By: Claude` from all 34 commits
  (the user is the sole author); don't reintroduce it.
- `main` is **branch-protected**: no force-push, no deletion. Normal pushes are
  allowed, so a PR isn't strictly required — but recent work has gone through
  PRs (#2–#5) and that reads well on a public repo.
- Verify features by **driving the real app** (screenshots), not just tests, for
  anything with a runtime surface — then commit. This session, on-screen checks
  caught three bugs that compiled and passed 118 tests.
- **The Store is the promoted install path.** GitHub carries the portable zip
  only, shipped unsigned with the SAC/SmartScreen limitation documented honestly.
  Store builds are signed by Microsoft.
- Danial is **new to C#/.NET** — explain non-obvious concepts (P/Invoke, async
  void, XAML binding, MSIX) while building.
- Plan files from past sessions live in `~\.claude\plans\`.
- **This file is public.** Keep local paths, account addresses and anything else
  personal out of it — it ships in the repo like any other source file.

---

## 13. Current state (2026-08-09)

- Working branch **`v0.6.0`**, branched from `origin/main` (which was ahead of
  the local `main` — PR #10 had merged there). Seven commits, listed in §9.
- **Nothing has been published.** No tag, no GitHub release, no Store bundle.
  Per §12 that all waits for the user.
- **312 tests passing**; x64 and ARM64 Release both build clean.
- **PDFium 153.0.7988** (was 152.0.7961).
- **Package set changed** — `Microsoft.WindowsAppSDK` is no longer referenced;
  see §7 before upgrading the SDK. Portable zip measured at **69.4 MB** (was
  ~88 MB); the Store bundle has not been rebuilt, so its new size is unmeasured.
- **Version bumped to 0.6.0** in both `Rune.App.csproj` and
  `Package.appxmanifest`.

### Still to do before submitting

1. **Check the live Store description in Partner Center.** `winget show --id
   9NH37840QDM6 --source msstore` reads back a description with no
   forms/signing/flatten bullets that does **not** open with the .NET / Windows
   App SDK disclosure. The 30 July certification report ("Pass with required
   fix", policy 10.2.4.1) requires that disclosure in the first two lines. The
   corrected copy is in `docs/store-listing.md`; it may never have been pasted
   in. This is a Partner Center UI check and cannot be done from the repo.
2. **Two Store screenshots** still unshot (page-editing sidebar, shortcuts
   overlay) — see `docs/store-listing.md`. §8b's rules apply: empty Rune
   profile, licence-safe document, 1920×1080.
3. **Store listing copy** needs the v0.6.0 additions: extract, typed
   signatures, and that everything now works while rotated.
4. **GitHub Releases is still at v0.4.0** while the Store shipped 0.5.0. 0.4.1,
   0.5.0 and 0.5.1 were never tagged; simplest honest option is to publish
   v0.6.0 and say in the notes that the intervening versions went out through
   the Store.


### Verified by driving the app

Everything below was checked on screen, not just by test:

- **Rotation, both directions.** A find hit lands exactly on its word and moves
  to the correct corner as the view turns (top-left → top-right → bottom-right →
  bottom-left), and hits survive the turn instead of being cleared. Drag-to-select
  was then checked at all four rotations: the selection wash lands on the glyphs,
  following the text line whichever way it runs.
- **Typed signatures**, end to end: typed, saved to the reusable list, placed on
  the page with transparency intact.
- **Signature resize**, end to end: a stamp already in the file, selected with a
  plain click, corner handles drawn, bottom-right handle dragged. 259×86 → 512×179
  on screen, with the ink filling the new box rather than the frame growing around
  an unchanged image.
- **The trimmed package**, by launching it — including night mode, the sharpest
  single check because it runs through Win2D's `InvertEffect`.

Not driven on screen: form-field filling while rotated. The arithmetic is the
same `PageRotationTransform` every other path uses, and
`PageRotationParityTests` cross-checks it against PDFium's own
`FPDF_DeviceToPage` at all four rotations, but nobody has clicked a rotated form
field.

### Notes on the screenshot harness

A DPI-aware `SendInput` + `PrintWindow` driver lives in the session scratchpad.
Three traps cost real time here and will again if it is rebuilt:

1. **The `INPUT` struct must be exactly 40 bytes on x64.** Any trailing padding
   makes `SendInput` fail with `ERROR_INVALID_PARAMETER`, silently — the cursor
   still moves because `SetCursorPos` is a separate call, so it looks like the
   app is ignoring clicks.
2. **Declare per-monitor DPI awareness before anything else.** Without it
   `GetWindowRect` returns logical coordinates while `PrintWindow` renders
   physical pixels, so on this 125% display every capture is cropped to the left
   ~80% of the window. That cost an hour: it looks exactly like the header's
   right-hand buttons having vanished, and every coordinate read off such a
   capture is then wrong, which in turn looks like pointer input not reaching the
   Win2D canvas. **It does reach it** — drags, clicks and right-clicks on the
   page all work.
3. **Bound pixel searches to the page.** The document background is dark in this
   theme, so a naive "find the dark pixels" glyph search matches the app's own
   chrome, and a page-bounded search still catches the gap between two pages.
   Locating text by running a find and looking for the highlight colour is far
   more robust than looking for dark pixels.
