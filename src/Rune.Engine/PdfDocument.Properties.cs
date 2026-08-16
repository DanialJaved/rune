using System.Globalization;
using Rune.PdfiumInterop;

namespace Rune.Engine;

// Everything the properties dialog shows.
//
// It used to be eight metadata tags, the PDF version, the page count and the
// file size — and it dropped any tag that was blank, so a document with no
// author simply had no Author row and you could not tell whether it was missing
// or whether Rune had not looked. Blanks are shown as blanks here for exactly
// that reason: an empty field is an answer.
//
// The expensive part is the font scan, which has to walk page objects, so it is
// a separate call the shell makes after the dialog is already up.
public sealed partial class PdfDocument
{
    /// <summary>
    /// Everything about the document that can be answered without walking its
    /// pages, grouped for display. Fonts come from
    /// <see cref="GetFontsUsed"/> separately.
    /// </summary>
    /// <param name="currentPage">
    /// Which page's size to report. Documents whose pages differ get a note
    /// saying so, since one number would otherwise be a lie about the rest.
    /// </param>
    public IReadOnlyList<PropertySection> GetDocumentProperties(int currentPage = 0)
    {
        var meta = new Dictionary<string, string>();
        int version;
        uint permissions;
        int securityRevision;
        bool tagged;
        int attachments;

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);

            foreach (string tag in (string[])
                     [
                         "Title", "Author", "Subject", "Keywords",
                         "Creator", "Producer", "CreationDate", "ModDate",
                     ])
            {
                meta[tag] = PdfiumNative.GetMetaText(_handle, tag);
            }

            version = PdfiumNative.GetFileVersion(_handle);
            permissions = PdfiumNative.GetDocPermissions(_handle);
            securityRevision = PdfiumNative.GetSecurityRevision(_handle);
            tagged = PdfiumNative.IsTagged(_handle);
            attachments = PdfiumNative.GetAttachmentCount(_handle);
        }

        return
        [
            new PropertySection("Description",
            [
                ("Title", Or(meta["Title"])),
                ("Author", Or(meta["Author"])),
                ("Subject", Or(meta["Subject"])),
                ("Keywords", Or(meta["Keywords"])),
            ]),
            new PropertySection("Origin",
            [
                ("Created", Date(meta["CreationDate"])),
                ("Modified", Date(meta["ModDate"])),
                ("Created with", Or(meta["Creator"])),
                ("Produced by", Or(meta["Producer"])),
            ]),
            new PropertySection("Pages", PageRows(currentPage)),
            new PropertySection("Security", SecurityRows(permissions, securityRevision)),
            new PropertySection("Features",
            [
                ("Tagged", tagged ? "Yes" : "No"),
                ("Form", FormKind switch
                {
                    PdfFormKind.AcroForm => "AcroForm (fillable)",
                    PdfFormKind.Xfa => "XFA (not fillable in Rune)",
                    _ => "None",
                }),
                ("Attachments", attachments.ToString(CultureInfo.CurrentCulture)),
            ]),
            new PropertySection("File", FileRows(version)),
        ];

        static string Or(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

        static string Date(string value) => PdfDate.TryParse(value) is { } when
            ? when.LocalDateTime.ToString("f", CultureInfo.CurrentCulture)
            // Not parseable but not empty: show what the file actually says
            // rather than swallowing it.
            : Or(value);
    }

    private List<(string, string)> PageRows(int currentPage)
    {
        var rows = new List<(string, string)>
        {
            ("Count", PageCount.ToString(CultureInfo.CurrentCulture)),
        };

        if (PageCount == 0)
        {
            return rows;
        }

        int page = Math.Clamp(currentPage, 0, PageCount - 1);
        var (width, height) = GetPageSize(page);
        rows.Add(($"Page {page + 1}", PaperSize.Describe(width, height)));

        // Whether the rest match. Capped, because a thousand-page book would
        // otherwise pay for a question the answer to is almost always "yes" —
        // and a document that changes size does it early far more often than it
        // does it on page 900.
        int sampled = Math.Min(PageCount, 200);
        for (int i = 0; i < sampled; i++)
        {
            var (w, h) = GetPageSize(i);
            if (Math.Abs(w - width) > 1 || Math.Abs(h - height) > 1)
            {
                rows.Add(("Note", sampled < PageCount
                    ? $"Pages are not all the same size (checked the first {sampled})"
                    : "Pages are not all the same size"));
                break;
            }
        }
        return rows;
    }

    /// <summary>
    /// The permission bits, spelled out. PDF 32000-1 table 22 numbers them from
    /// 1, so bit 3 is the value 4 — the off-by-one that makes reading this table
    /// against the spec confusing, hence the values here rather than the
    /// numbers.
    ///
    /// An unencrypted document has every bit set, so this reads as "everything
    /// allowed" without a special case. Worth stating plainly all the same: the
    /// flags are a request to the reader, not enforcement. Rune honours nothing
    /// here and neither does anything else without the owner password.
    /// </summary>
    private List<(string, string)> SecurityRows(uint permissions, int revision)
    {
        bool encrypted = revision >= 0;
        var rows = new List<(string, string)>
        {
            ("Encrypted", encrypted ? $"Yes (revision {revision})" : "No"),
        };

        if (!encrypted)
        {
            rows.Add(("Permissions", "Everything allowed"));
            return rows;
        }

        var allowed = new List<string>();
        foreach (var (bit, label) in new (uint, string)[]
        {
            (0x0004, "print"),
            (0x0800, "print at high resolution"),
            (0x0008, "change the content"),
            (0x0010, "copy text"),
            (0x0020, "annotate"),
            (0x0100, "fill in forms"),
            (0x0200, "copy for accessibility"),
            (0x0400, "reorder pages"),
        })
        {
            if ((permissions & bit) != 0)
            {
                allowed.Add(label);
            }
        }

        rows.Add(("Allowed", allowed.Count > 0 ? string.Join(", ", allowed) : "Nothing"));
        return rows;
    }

    private List<(string, string)> FileRows(int version)
    {
        var rows = new List<(string, string)>
        {
            ("Name", Path.GetFileName(FilePath)),
            ("Folder", Path.GetDirectoryName(FilePath) ?? "—"),
        };

        if (version > 0)
        {
            rows.Add(("PDF version", $"{version / 10}.{version % 10}"));
        }

        try
        {
            var info = new FileInfo(FilePath);
            long bytes = info.Length;
            rows.Add(("Size", bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
                _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
            }));
            rows.Add(("Last written", info.LastWriteTime.ToString("f", CultureInfo.CurrentCulture)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file may have moved or been locked since it was opened. Its
            // size on disk is the least of what this dialog is for.
        }

        return rows;
    }

    /// <summary>
    /// Every font the document draws with, deduplicated by name.
    ///
    /// Walks page objects, recursing one level into form XObjects — a great deal
    /// of real text lives inside those, and a scan that skipped them would
    /// report a font-free page for plenty of ordinary files.
    ///
    /// Capped at <paramref name="maxPages"/>, because this is the one thing in
    /// the dialog whose cost grows with the document: a thousand-page book has
    /// hundreds of thousands of objects and its font list stopped changing on
    /// page four. The caller says so in the section heading when the cap bites.
    /// </summary>
    public IReadOnlyList<FontUsage> GetFontsUsed(int maxPages, CancellationToken cancel = default)
    {
        var found = new Dictionary<string, FontUsage>(StringComparer.Ordinal);
        int pages = Math.Min(PageCount, Math.Max(0, maxPages));

        for (int i = 0; i < pages; i++)
        {
            cancel.ThrowIfCancellationRequested();

            lock (PdfiumLibrary.Lock)
            {
                ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
                IntPtr page = AcquirePageLocked(i);
                if (page == IntPtr.Zero)
                {
                    continue; // an unreadable page is not a reason to abandon the scan
                }
                try
                {
                    int count = PdfiumNative.CountPageObjects(page);
                    for (int o = 0; o < count; o++)
                    {
                        CollectFontsLocked(PdfiumNative.GetPageObject(page, o), found, depth: 0);
                    }
                }
                finally
                {
                    ReleasePageLocked(i);
                }
            }
        }

        return [.. found.Values.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// Adds one object's font, or recurses into it. Caller must hold
    /// <see cref="PdfiumLibrary.Lock"/> and a lease on the page.
    /// </summary>
    private static void CollectFontsLocked(IntPtr obj, Dictionary<string, FontUsage> found, int depth)
    {
        if (obj == IntPtr.Zero)
        {
            return;
        }

        if (PdfiumNative.IsFormObject(obj))
        {
            // One level of nesting, not arbitrary depth: form XObjects can
            // reference each other, and a malformed file can make that a cycle.
            if (depth >= 2)
            {
                return;
            }
            int nested = PdfiumNative.CountFormObjects(obj);
            for (int i = 0; i < nested; i++)
            {
                CollectFontsLocked(PdfiumNative.GetFormObject(obj, i), found, depth + 1);
            }
            return;
        }

        if (!PdfiumNative.IsTextObject(obj)
            || PdfiumNative.DescribeTextObjectFont(obj) is not { } font
            || string.IsNullOrWhiteSpace(font.Name))
        {
            return;
        }

        found.TryAdd(font.Name, new FontUsage(font.Name, font.Embedded, DescribeFontFlags(font.Flags)));
    }

    /// <summary>
    /// The font descriptor's /Flags as something readable. PDF 32000-1 table
    /// 123; -1 when PDFium cannot say, which is common for the standard 14
    /// because they carry no descriptor at all.
    /// </summary>
    private static string DescribeFontFlags(int flags)
    {
        if (flags <= 0)
        {
            return "—";
        }

        var parts = new List<string>();
        if ((flags & 1) != 0) { parts.Add("monospace"); }
        if ((flags & 2) != 0) { parts.Add("serif"); }
        if ((flags & 8) != 0) { parts.Add("script"); }
        if ((flags & 4) != 0 && (flags & 32) == 0) { parts.Add("symbolic"); }
        if ((flags & 64) != 0) { parts.Add("italic"); }
        if ((flags & 0x40000) != 0) { parts.Add("bold"); }

        return parts.Count > 0 ? string.Join(", ", parts) : "—";
    }
}
