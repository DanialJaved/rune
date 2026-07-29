# Privacy Policy — Rune PDF Reader

_Last updated: 29 July 2026_

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
| An error log, if something goes wrong | To help diagnose a crash |

You can delete all of it at any time by deleting the `%LOCALAPPDATA%\Rune`
folder. Uninstalling Rune from the Microsoft Store removes it automatically.

## Network access

**The Microsoft Store version of Rune makes no network requests at all.**

The portable version downloaded from GitHub can check GitHub's public releases
API to see whether a newer version exists. That check sends nothing but an
ordinary web request; it includes no personal information and no data about the
documents you have open. It can be turned off in Settings, and it is disabled
entirely in the Store build because the Store handles updates.

## Your documents

Rune opens PDF files from your device and renders them locally. Documents are
never uploaded, indexed, or transmitted anywhere. Annotations and page edits are
written back only to the file you choose to save, on your own device.

## Children

Rune is a document viewer suitable for all ages and does not knowingly collect
information from anyone, including children.

## Changes

Any change to this policy will be committed to this repository, so the history is
public and auditable.

## Contact

Questions or concerns: https://github.com/DanialJaved/rune/issues
