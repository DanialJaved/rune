using Rune.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Rune.Services;

/// <summary>
/// Hands the open PDF to another app through the Windows share sheet.
///
/// Modelled on <see cref="PrintService"/>, and for the same reason: the share
/// sheet is a WinRT surface that in a desktop app has to be told which window it
/// belongs to. <c>DataTransferManager.GetForCurrentView</c> throws outright here
/// — there is no CoreWindow — so both the manager and the show call go through
/// the interop statics, exactly as printing does.
///
/// This is the only part of Rune that sends anything anywhere, and it sends it
/// to an app the user picks from the sheet. Nothing leaves the machine unless
/// they choose something that takes it there.
/// </summary>
public sealed class ShareService
{
    private readonly nint _hwnd;
    private DataTransferManager? _manager;

    /// <summary>What this share hands over, set just before the sheet opens.</summary>
    private StorageFile? _file;
    private string _title = "";

    public ShareService(nint hwnd) => _hwnd = hwnd;

    /// <summary>
    /// Where a copy goes when the document has edits that are not on disk yet.
    /// Its own directory so the sweep cannot touch anything else.
    /// </summary>
    private static string ShareDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Rune", "share");

    /// <summary>
    /// Opens the share sheet on <paramref name="path"/>.
    ///
    /// <paramref name="unsaved"/> is the open document when it has edits that
    /// are not on disk yet. A copy of it goes out instead of the file, under the
    /// document's own name, so what the other app receives is what is on screen.
    /// The original is never written to — <see cref="PdfDocument.SaveAs"/>
    /// leaves the open handle alone, which is why saving in place has to do the
    /// close-and-swap dance and this does not.
    /// </summary>
    public async Task ShowAsync(string path, string title, PdfDocument? unsaved, IPdfWorkQueue workQueue)
    {
        string toShare = path;
        if (unsaved is not null)
        {
            toShare = await WriteShareCopyAsync(path, unsaved, workQueue);
        }

        _file = await StorageFile.GetFileFromPathAsync(toShare);
        _title = title;

        if (_manager is null)
        {
            _manager = DataTransferManagerInterop.GetForWindow(_hwnd);
            _manager.DataRequested += Manager_DataRequested;
        }

        DataTransferManagerInterop.ShowShareUIForWindow(_hwnd);
    }

    private void Manager_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        if (_file is not { } file)
        {
            return;
        }

        var data = args.Request.Data;
        // A title is required — the sheet shows an error card without one.
        data.Properties.Title = _title;
        data.Properties.Description = file.Name;
        data.SetStorageItems([file]);
    }

    /// <summary>
    /// Writes the document, edits and all, next to nothing that matters.
    ///
    /// Named after the original so the receiving app shows "Contract.pdf" rather
    /// than a temp name, which means one copy per document and a sweep of
    /// yesterday's on the way in.
    /// </summary>
    private static async Task<string> WriteShareCopyAsync(
        string path, PdfDocument document, IPdfWorkQueue workQueue)
    {
        string directory = ShareDirectory;
        Directory.CreateDirectory(directory);
        SweepOldCopies(directory);

        string copy = Path.Combine(directory, Path.GetFileName(path));

        // The PDFium lock can be held by a tile render for tens of milliseconds,
        // so the write goes to the render thread like every other document call.
        await workQueue.RunAsync(PdfWorkPriority.Interactive, () =>
        {
            document.SaveAs(copy);
            return true;
        });
        return copy;
    }

    /// <summary>
    /// Drops copies from earlier sessions. Best-effort: a file the receiving app
    /// still has open cannot be deleted, and that is not worth failing a share
    /// over — it will go on the next sweep instead.
    /// </summary>
    private static void SweepOldCopies(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (string file in Directory.EnumerateFiles(directory, "*.pdf"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open in whatever it was shared with.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Default.Write(nameof(ShareService), ex);
        }
    }
}
