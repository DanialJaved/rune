# Security policy

Rune opens PDFs, and a PDF is untrusted input that often arrives by email or
from a web page. Parser bugs in that position matter, so please report them
rather than filing them in public.

## Reporting a vulnerability

Use GitHub's **[private vulnerability reporting](https://github.com/DanialJaved/rune/security/advisories/new)**
on this repository. It is private between you and the maintainer until a fix
ships.

Please include the PDF that triggers it if you can share one, the Rune version
(Settings shows it), and whether you installed from the Microsoft Store or the
portable zip. A file that reproduces the problem is worth more than anything
else you can send.

Rune is maintained by one person as an unpaid project. Expect a first reply
within about a week. There is no bounty.

Please do not open a public issue for something exploitable, and please do not
post a proof-of-concept file publicly before a fix is out.

## Supported versions

The latest release only. Rune is small and ships often; fixes go into the next
version rather than being backported. The Store build updates itself, and it is
the recommended way to stay current.

## Where the bug probably lives

Rune renders with [PDFium](https://pdfium.googlesource.com/pdfium/), the same
engine Chrome uses, through a thin P/Invoke layer. That split matters for
reporting:

- **A malformed PDF that crashes the renderer** is most likely a PDFium bug.
  Those are best reported to the [Chromium tracker](https://issues.chromium.org/),
  where they reach the people who maintain the parser and get picked up by every
  downstream project. Tell us too, so Rune can pin a fixed build.
- **Anything in Rune's own code** is ours: the P/Invoke layer, file handling,
  saving, annotation and signature writing, and the state and signature stores
  under `%LOCALAPPDATA%\Rune`. Report those here.

Rune keeps its PDFium pin current for exactly this reason: PDFium releases carry
Chrome's PDF security fixes, and a stale pin is the largest avoidable risk in the
project.

## What Rune does not do

These are design decisions, not gaps, and they bound the attack surface:

- **No networking.** Rune contains no networking code in any build. It makes no
  requests, checks for no updates, and sends no telemetry. If you find Rune
  making a network connection, that is a bug worth reporting immediately.
- **No form JavaScript.** Rune links a PDFium build without V8, so script
  embedded in a PDF never runs. Auto-calculating form fields keep what you type
  but do not recalculate.
- **No signature validation.** Rune reads back what a signed document claims and
  reports whether the signature covers the whole file. It ships no cryptography
  and never calls a signature valid. Do not treat anything Rune shows as
  verification.
- **No elevation, no background tasks, no system settings.** Rune runs as a
  normal user process and touches only the files you open and its own state
  directory.
