using Windows.Storage;
using Windows.Storage.Pickers;

namespace Rune.Services;

/// <summary>
/// The result of asking for a file: one of picked, cancelled, or failed.
///
/// Callers have to tell cancelled from failed. A cancel is the user changing
/// their mind and must stay completely silent; a failure is Windows not doing
/// its job and must say so, or it gets reported as a problem with the file the
/// user never got as far as choosing.
/// </summary>
public readonly record struct PickedFile(StorageFile? File, bool Failed)
{
    public bool Cancelled => File is null && !Failed;
}

/// <summary>
/// Every file picker in the app goes through here.
///
/// Windows 11 brokers these out of process: each call spawns a fresh
/// <c>PickerHost.exe</c>, and when that activation fails
/// <c>PickSingleFileAsync</c> throws <c>COMException 0x80004005</c> (E_FAIL)
/// with no dialog ever appearing. That happened three times in a row on a real
/// v0.6.0 build (PROJECT.md §10) and has not reproduced since across every
/// path, repeat and foreground race that was tried, which is the signature of a
/// transient broker failure rather than a logic error in Rune.
///
/// So this does the two things that help against a transient: it retries once,
/// and it fails honestly if the retry fails too. Both attempts are logged, so a
/// recurrence leaves evidence of whether the retry saved it.
///
/// It also owns <c>InitializeWithWindow</c>, which is mandatory in WinUI 3 —
/// a picker with no owner window never opens at all.
/// </summary>
public static class FilePickerHost
{
    private const string Source = "FilePickerHost";

    /// <summary>Shown when the picker could not be opened. Deliberately says to try again: it usually works.</summary>
    public const string FailureMessage =
        "Windows could not open the file picker. Please try that again.";

    /// <summary>
    /// Long enough for a failed broker activation to finish falling over, short
    /// enough that a user who is about to see a file dialog does not notice.
    /// </summary>
    private const int RetryDelayMs = 250;

    /// <summary>Asks for one existing file, e.g. <c>".pdf"</c>.</summary>
    public static Task<PickedFile> PickOpenAsync(nint owner, params string[] fileTypes) =>
        RunAsync(() =>
        {
            var picker = new FileOpenPicker();
            foreach (string type in fileTypes)
            {
                picker.FileTypeFilter.Add(type);
            }
            WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
            return picker.PickSingleFileAsync().AsTask();
        });

    /// <summary>Asks where to write a file. <paramref name="typeLabel"/> names the format in the dialog.</summary>
    public static Task<PickedFile> PickSaveAsync(
        nint owner, string suggestedName, string typeLabel, params string[] extensions) =>
        RunAsync(() =>
        {
            var picker = new FileSavePicker { SuggestedFileName = suggestedName };
            picker.FileTypeChoices.Add(typeLabel, extensions);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
            return picker.PickSaveFileAsync().AsTask();
        });

    /// <summary>
    /// Runs a picker, once more if the first attempt throws.
    ///
    /// The factory builds a fresh picker per attempt on purpose: an object whose
    /// activation just failed is not worth reusing.
    /// </summary>
    private static async Task<PickedFile> RunAsync(Func<Task<StorageFile?>> show)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new PickedFile(await show(), false);
            }
            catch (Exception ex) when (attempt == 0)
            {
                ErrorLog.Default.Write(Source, $"Picker failed, retrying once. {ex}");
                await Task.Delay(RetryDelayMs);
            }
            catch (Exception ex)
            {
                ErrorLog.Default.Write(Source, ex);
                return new PickedFile(null, true);
            }
        }
    }
}
