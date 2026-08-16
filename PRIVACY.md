# Privacy Policy — Rune PDF Reader

_Last updated: 12 August 2026_

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
| A copy of a document you shared with unsaved edits, in `share\` | So the app you sent it to receives what you were looking at |
| An error log, if something goes wrong | To help diagnose a crash |

Copies in `share\` are deleted an hour after they are written, on the next
share. You can delete the folder at any time.

**Your signature never leaves the device.** It is stored as an ordinary image
file in `%LOCALAPPDATA%\Rune\signatures`, is never uploaded or transmitted, and
is only written into a PDF when you place it on a page yourself. You can delete
any saved signature from the Sign tool, or by deleting that folder.

When you import a photo of your signature, Rune removes the paper background
itself, on your machine. No online service, no AI model and no upload is
involved — the photo is read, processed and saved without ever leaving Rune.

You can delete all of it at any time by deleting the `%LOCALAPPDATA%\Rune`
folder. Uninstalling Rune from the Microsoft Store removes it automatically.

## Network access

**Rune makes no network requests at all.**

Not on startup, not while you read, not ever — there is no networking code left
in the application. Earlier versions of the portable build could ask GitHub
whether a newer release existed; that check was removed, and updates now come
from the Microsoft Store.

Share is not an exception. It hands the file to Windows, which hands it to the
app you chose; Rune opens no connection of its own. Whether the file then goes
anywhere is up to that app.

## Your documents

Rune opens PDF files from your device and renders them locally. Documents are
never uploaded, indexed, or transmitted anywhere. Annotations, filled form
fields and page edits are written back only to the file you choose to save, on
your own device.

**Form data stays in the document.** Rune fills PDF forms locally and never
submits them anywhere. A PDF's own Submit button is not wired up: Rune does not
execute form actions or form JavaScript, so nothing you type into a form can be
sent over the network by the document itself.

**Sharing is the one thing that hands a document to something else, and only
when you ask.** Share opens the Windows share sheet and gives the PDF to the app
you pick from it. Rune does not choose the destination, does not upload
anything itself, and has no say in what the receiving app does next — if you
pick a mail or cloud app, the file goes wherever that app sends it. If the
document has edits you have not saved, Rune shares a copy that includes them
rather than the older file on disk; your original is left untouched.

## Children

Rune is a document viewer suitable for all ages and does not knowingly collect
information from anyone, including children.

## Changes

Any change to this policy will be committed to this repository, so the history is
public and auditable.

## Contact

Questions or concerns: https://github.com/DanialJaved/rune/issues
