using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Rune.Controls;
using Rune.Engine;
using Rune.Services;
using Windows.UI;

namespace Rune;

// The text tool's shell half: the toolbar button, and the format bar that
// floats over the page while a box is open.
//
// The bar is built in code rather than markup so the font and size lists come
// from TextBoxFonts alone. Two copies of those lists would eventually disagree,
// and the one in markup would be the one nobody updated.
public sealed partial class MainWindow
{
    private bool _textBarBuilt;

    /// <summary>The style new boxes take, remembered between them and across sessions.</summary>
    private TextBoxStyle CurrentTextStyle
    {
        get
        {
            var saved = _state.Settings.TextBox;
            return saved is null
                ? TextBoxStyle.Default
                : new TextBoxStyle(
                    Enum.TryParse<PdfStandardFont>(saved.Font, out var f) ? f : PdfStandardFont.Helvetica,
                    saved.Size, saved.R, saved.G, saved.B, saved.Bold, saved.Italic);
        }
    }

    private void PersistTextStyle(TextBoxStyle style)
    {
        _state.Settings.TextBox = new TextBoxSettings
        {
            Font = style.Font.ToString(),
            Size = style.FontSize,
            R = style.R,
            G = style.G,
            B = style.B,
            Bold = style.Bold,
            Italic = style.Italic,
        };
        _store.Save(_state);
    }

    private void TextToolButton_Click(object sender, RoutedEventArgs e)
    {
        bool on = _activeViewer?.ActiveTool == AnnotationTool.Text;
        SetActiveTool(on ? AnnotationTool.None : AnnotationTool.Text);
    }

    /// <summary>
    /// Wires a viewer's text events. Called from the same place every other
    /// per-view subscription is made.
    /// </summary>
    private void AttachTextTool(PdfViewer viewer)
    {
        viewer.SetTextStyle(CurrentTextStyle);
        viewer.TextEditingChanged += Viewer_TextEditingChanged;
        // Placing a box is a one-shot action, like a sticky note: the tool drops
        // after it commits rather than arming the next click as another box.
        viewer.TextCommitted += (_, _) => SetActiveTool(AnnotationTool.None);
    }

    private void DetachTextTool(PdfViewer viewer) =>
        viewer.TextEditingChanged -= Viewer_TextEditingChanged;

    private void Viewer_TextEditingChanged(object? sender, bool editing)
    {
        if (!ReferenceEquals(sender, _activeViewer))
        {
            return;
        }
        if (editing)
        {
            BuildTextFormatBar();
        }
        TextFormatBar.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the format bar once. Rebuilding it per box would drop whatever the
    /// user was mid-interaction with, and the controls are stateless besides.
    /// </summary>
    private void BuildTextFormatBar()
    {
        if (_textBarBuilt)
        {
            SyncTextFormatBar();
            return;
        }
        _textBarBuilt = true;

        // Claim the box's commit BEFORE focus moves. A pointer press arrives
        // ahead of both the editor's LostFocus and the bar's GotFocus, which is
        // the only ordering that gets in front of the commit. handledEventsToo,
        // because the controls themselves mark the press handled.
        TextFormatBar.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) =>
            {
                if (_activeViewer is { IsEditingText: true } v)
                {
                    v.SuspendTextCommit = true;
                }
            }),
            handledEventsToo: true);

        var family = new ComboBox { MinWidth = 116, VerticalAlignment = VerticalAlignment.Center };
        foreach (var font in TextBoxFonts.Families)
        {
            family.Items.Add(TextBoxFonts.DisplayName(font));
        }
        family.SelectionChanged += (_, _) =>
        {
            if (family.SelectedIndex >= 0)
            {
                Restyle(s => s with { Font = TextBoxFonts.Families[family.SelectedIndex] });
            }
        };
        ToolTipService.SetToolTip(family, "Font");

        var size = new ComboBox { MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
        foreach (double pt in TextBoxFonts.Sizes)
        {
            size.Items.Add(pt.ToString("0.##"));
        }
        size.SelectionChanged += (_, _) =>
        {
            if (size.SelectedIndex >= 0)
            {
                Restyle(s => s with { FontSize = TextBoxFonts.Sizes[size.SelectedIndex] });
            }
        };
        ToolTipService.SetToolTip(size, "Size");

        var bold = new ToggleButton { Content = "B", MinWidth = 36, FontWeight = Microsoft.UI.Text.FontWeights.Bold };
        bold.Click += (_, _) => Restyle(s => s with { Bold = bold.IsChecked == true });
        ToolTipService.SetToolTip(bold, "Bold");

        var italic = new ToggleButton
        {
            Content = "I",
            MinWidth = 36,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
        };
        italic.Click += (_, _) => Restyle(s => s with { Italic = italic.IsChecked == true });
        ToolTipService.SetToolTip(italic, "Italic");

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (label, r, g, b) in FormTextColors)
        {
            byte cr = r, cg = g, cb = b;
            var dot = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, cr, cg, cb)),
            };
            var swatch = new Button
            {
                Style = (Style)Application.Current.Resources["InkSwatchButtonStyle"],
                Content = dot,
            };
            swatch.Click += (_, _) => Restyle(s => s with { R = cr, G = cg, B = cb });
            ToolTipService.SetToolTip(swatch, label);
            swatches.Children.Add(swatch);
        }

        TextFormatControls.Children.Add(family);
        TextFormatControls.Children.Add(size);
        TextFormatControls.Children.Add(bold);
        TextFormatControls.Children.Add(italic);
        TextFormatControls.Children.Add(new Border { Style = (Style)Application.Current.Resources["HeaderSeparatorStyle"] });
        TextFormatControls.Children.Add(swatches);

        _textFamilyBox = family;
        _textSizeBox = size;
        _textBoldButton = bold;
        _textItalicButton = italic;
        SyncTextFormatBar();
    }

    private ComboBox? _textFamilyBox;
    private ComboBox? _textSizeBox;
    private ToggleButton? _textBoldButton;
    private ToggleButton? _textItalicButton;

    /// <summary>Points the bar's controls at the current style without raising their events.</summary>
    private void SyncTextFormatBar()
    {
        var style = _activeViewer?.TextStyle ?? CurrentTextStyle;
        _syncingTextBar = true;
        try
        {
            if (_textFamilyBox is { } f)
            {
                f.SelectedIndex = Math.Max(0, Array.IndexOf(TextBoxFonts.Families, style.Font));
            }
            if (_textSizeBox is { } s)
            {
                int i = Array.FindIndex(TextBoxFonts.Sizes, v => Math.Abs(v - style.FontSize) < 0.01);
                s.SelectedIndex = i >= 0 ? i : Array.IndexOf(TextBoxFonts.Sizes, 14);
            }
            if (_textBoldButton is { } b) { b.IsChecked = style.Bold; }
            if (_textItalicButton is { } it) { it.IsChecked = style.Italic; }
        }
        finally
        {
            _syncingTextBar = false;
        }
    }

    private bool _syncingTextBar;

    /// <summary>
    /// Applies a change to the open box and remembers it. Live, because seeing
    /// the change on the page is the point of having the bar at all.
    /// </summary>
    private void Restyle(Func<TextBoxStyle, TextBoxStyle> change)
    {
        if (_syncingTextBar || _activeViewer is not { } viewer)
        {
            return;
        }
        var style = change(viewer.TextStyle);
        viewer.SetTextStyle(style);
        PersistTextStyle(style);

        // Straight back to typing. This also lifts the commit suspension, so the
        // box behaves normally again the moment the trip to the bar is over.
        viewer.FocusTextEditor();
    }
}
