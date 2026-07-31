using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Rune.Controls;

/// <summary>
/// The app's one notice surface: a compact floating card over whatever it is
/// placed on top of.
///
/// Two instances exist — one inside each <see cref="DocumentView"/>, bounded to
/// the page area so it can never reach the sidebar, and one at window level for
/// messages that arrive with no document open (startup problems, a missing
/// recent file, background failures).
///
/// It wraps <see cref="InfoBar"/> rather than hand-rolling chrome: severity
/// already carries the right icon, colour and screen-reader semantics. What was
/// wrong before was never the InfoBar, only that nothing bounded its width.
/// </summary>
public sealed partial class NoticeHost : UserControl
{
    /// <summary>
    /// Notices the user has closed, by key. A notice describes a property of
    /// the document (it is signed; it is an XFA form) rather than an event, so
    /// re-announcing it after every page edit or tab switch is nagging — but
    /// the host is per-DocumentView, so closing and reopening the tab reasonably
    /// shows it again.
    /// </summary>
    private readonly HashSet<string> _dismissed = [];

    public NoticeHost()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows a notice. Returns false when <paramref name="dismissKey"/> names a
    /// notice the user has already closed, in which case nothing is shown.
    /// </summary>
    /// <param name="title">Short lead-in, or null for a message-only notice.</param>
    /// <param name="dismissKey">
    /// Identity for "don't show me this again". Null means transient — always
    /// shown, and never remembered.
    /// </param>
    public bool Show(string? title, string message, InfoBarSeverity severity, string? dismissKey = null)
    {
        if (dismissKey is not null && _dismissed.Contains(dismissKey))
        {
            return false;
        }

        _currentKey = dismissKey;
        Bar.Severity = severity;
        Bar.Title = title ?? string.Empty;
        Bar.Message = message;
        Bar.IsOpen = true;
        Card.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>Hides the notice without recording a dismissal.</summary>
    public void Clear()
    {
        _currentKey = null;
        Bar.IsOpen = false;
        Card.Visibility = Visibility.Collapsed;
    }

    private string? _currentKey;

    private void Bar_CloseButtonClick(InfoBar sender, object args)
    {
        // Closing by hand is the signal that this one has been read.
        if (_currentKey is not null)
        {
            _dismissed.Add(_currentKey);
        }
        _currentKey = null;
        Card.Visibility = Visibility.Collapsed;
    }
}
