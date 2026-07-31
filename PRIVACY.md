# Privacy Policy — Rune PDF Reader

_Last updated: 31 July 2026_

**Rune does not collect, transmit, or share any personal data.**

There is no account, no telemetry, no analytics, no advertising, and no crash
reporting service. Nothing you open in Rune leaves your computer.

## What Rune stores, and where

Rune saves a small amount of state locally so it can pick up where you left off.
All of it lives in `%LOCALAPPDATA%\Rune` on your own machine:

| What | Why |
|---|---|
| Paths of recently-opened files, and their page-1 thumbnails | To show the recent-documents list |
| Per-document reading position, zoom and rotation | To reopen a document where you stopped |
| Bookmarks you create | To restore them next time you open that document |
| Your preferences (theme, sidebar, keyboard options) | To remember your settings |
| Signatures you draw or import, in `signatures\` | So you can reuse one instead of redrawing it |
| An error log, if something goes wrong | To help diagnose a crash |

**Your signature never leaves the device.** It is stored as an ordinary image
file in `%LOCALAPPDATA%\Rune\signatures`, is never uploaded or transmitted, and
is only written into a PDF when you place it on a page yourself. You can delete
any saved signature from the Sign tool, or by deleting that folder.

You can delete all of it at any time by deleting the `%LOCALAPPDATA%\Rune`
folder. Uninstalling Rune from the Microsoft Store removes it automatically.

## Network access

**Rune makes no network requests at all.**

Not on startup, not while you read, not ever — there is no networking code left
in the application. Earlier versions of the portable build could ask GitHub
whether a newer release existed; that check was removed, and updates now come
from the Microsoft Store.

## Your documents

Rune opens PDF files from your device and renders them locally. Documents are
never uploaded, indexed, or transmitted anywhere. Annotations, filled form
fields and page edits are written back only to the file you choose to save, on
your own device.

**Form data stays in the document.** Rune fills PDF forms locally and never
submits them anywhere. A PDF's own Submit button is not wired up: Rune does not
execute form actions or form JavaScript, so nothing you type into a form can be
sent over the network by the document itself.

## Children

Rune is a document viewer suitable for all ages and does not knowingly collect
information from anyone, including children.

## Changes

Any change to this policy will be committed to this repository, so the history is
public and auditable.

## Contact

Questions or concerns: https://github.com/DanialJaved/rune/issues
