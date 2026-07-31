# Microsoft Store listing — Rune PDF Reader

Copy-paste source for the Partner Center submission. Keep in sync with README.

- **Reserved name:** Rune PDF Reader
- **Package identity:** `Danimite.RunePDFReader`
- **Publisher:** `CN=513DE1BC-C862-44F8-AEAD-F60E359F4BBF`
- **Publisher display name:** Danimite
- **Product ID:** `9NH37840QDM6` — live at https://apps.microsoft.com/detail/9NH37840QDM6

> **Certification note (report of 30 July 2026, "Pass with required fix").**
> Policy **10.2.4.1 — Security / Software Dependencies**: a product that depends
> on non-integrated software must disclose it **within the first two lines of
> the Store description**. The v0.4.1 listing did not, and the next submission
> is required to. The Description below now opens with that disclosure — do not
> reorder it below the fold when editing.

---

## Short description (max 200 chars)

Built on .NET and the Windows App SDK, both included. A fast, free, open-source
PDF reader: instant open, smooth scrolling, forms, annotations and page editing.

*(157 chars)*

---

## Description (max 10,000 chars)

Rune PDF Reader is built on the Microsoft .NET runtime and the Windows App SDK. Both are included in the package, so there is nothing extra to download or install.

A fast, free, open-source PDF reader for Windows that opens instantly, scrolls smoothly through thousand-page documents, and stays out of your way. No account, no subscription, no telemetry, no ads.

**Fast**

Rune renders with PDFium — the same engine Chrome uses — on a dedicated thread, so scrolling never stutters and the window never freezes. Pages appear progressively: a fast preview first, then a crisp render. Large files stream from disk instead of loading into memory.

**Clean**

One slim toolbar. Everything else lives in a single menu, so the page gets the space. Tabs sit in the title bar, and the whole app follows your Windows light or dark theme.

**Complete**

• Tabs, with each document remembering where you stopped reading
• Sidebar with page thumbnails, chapters and your own bookmarks
• Text selection, copy, and find-in-document with highlight-all
• Form filling — click a field and type; text boxes, dropdowns and checkboxes all save back into the PDF (standard AcroForm documents; XFA forms are not supported)
• Sign a document — draw or import your signature, place it on the page, and reuse it next time. Signatures are stored only on your device
• Flatten — bake annotations and filled fields into the page so they can't be edited out
• Signature details — read what a digitally signed document reports, including whether the signature covers the whole file
• Annotations — highlight, underline, strikeout, sticky notes and freehand pen — saved as standard PDF annotations that any reader can see
• Page editing — reorder by dragging, delete, copy and paste pages between open documents, or drop a PDF into the sidebar to merge it in
• Undo and redo for everything
• Presentation mode (F5) for showing slides fullscreen
• Night mode that inverts page colours for comfortable reading in the dark
• Printing with live preview and page ranges
• Full keyboard control, including a command palette and an optional vim-style key set

**Open source**

Rune is GPLv3 and developed in the open at https://github.com/DanialJaved/rune — read the code, file an issue, or build it yourself.

---

## Search terms (max 7, 30 chars each)

1. pdf
2. pdf reader
3. pdf viewer
4. pdf annotator
5. open source pdf
6. document reader
7. pdf editor

---

## Category

Productivity  →  (secondary: Utilities & tools)

---

## Age rating questionnaire — answers

Everything below is **No**; Rune is a local document viewer.

| Question | Answer |
|---|---|
| Contains violence / sexual content / profanity / drugs | No |
| Contains user-generated content or sharing | No |
| Allows users to interact / communicate | No |
| Collects or shares personal information | No |
| Contains advertising | No |
| Contains in-app purchases or gambling | No |
| Accesses location | No |

Expected result: **everyone / 3+**.

---

## Privacy

**Rune collects no data whatsoever,** and makes no network requests at all —
there is no networking code in the application. The self-updater that used to
serve the portable build was removed; the Store handles updates.

Everything Rune stores stays on the device, in `%LOCALAPPDATA%\Rune`:
recently-opened file paths, per-document reading positions and bookmarks, and
user preferences.

Privacy policy URL (required field — point it at the repo's policy):
`https://github.com/DanialJaved/rune/blob/main/PRIVACY.md`

---

## Capability justification — `runFullTrust`

Partner Center flags this as a restricted capability and asks "Why do you need
the runFullTrust capability, and how will it be used in your product?".
Paste the following verbatim:

> Rune PDF Reader is a WinUI 3 / Windows App SDK desktop application, and every
> packaged Win32 desktop app of this type runs as a full-trust process, so
> runFullTrust is required simply for the app to launch. Rune needs it to render
> PDF pages via PDFium (pdfium.dll), a native C++ library called through
> P/Invoke, to read the PDF files a user explicitly opens and save annotations,
> filled form fields and signature stamps back to them, and to print. It makes
> no network connections, runs no
> background tasks, requires no elevation, modifies no system settings, and
> collects no user data. Rune is open source under the GPLv3 and the full source
> is at https://github.com/DanialJaved/rune

The "no network connections" claim is unconditional as of v0.5.0: `UpdateService`
was deleted, so no build of Rune contains networking code. Adding any would
invalidate this justification and the privacy declaration together.

---

## Support / contact

- Support: https://github.com/DanialJaved/rune/issues
- Website: https://github.com/DanialJaved/rune

---

## Screenshots

Store requires at least one 1366×768 or larger desktop screenshot; up to 10.
Recommended set, in this order:

1. A document open in light mode, sidebar showing thumbnails — the everyday view
2. Night mode on the same document
3. A form being filled in, with a field focused
4. The annotation pen panel open, with a highlight visible on the page
5. The page-editing sidebar with several pages selected
6. Presentation mode (F5), fullscreen
7. The keyboard shortcuts overlay (F1)
8. The recent-documents start page

Capture at 1920×1080, windowed (not maximized over a busy desktop), with a
neutral document — avoid anything containing personal data.
