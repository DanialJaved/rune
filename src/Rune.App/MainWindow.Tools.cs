using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rune.Controls;
using Rune.Engine;
using Rune.Services;
using Windows.UI;

namespace Rune;

/// <summary>
/// The annotation tool cluster's options panels — the Snipping-Tool-style
/// colour grid, size and opacity that drop down beneath whichever tool button
/// was clicked.
///
/// One <see cref="Flyout"/> per tool, built lazily and then only *mutated*:
/// replacing the visual tree of an open flyout closes it, which is why nothing
/// here rebuilds. And a plain Flyout, not a MenuFlyout — a MenuFlyout dismisses
/// the moment any item is clicked, and this panel has to stay put while the
/// user tries colours.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// Swatches, laid out 6 to a row. Enough range to be useful without turning
    /// into a colour picker; the top row covers the greys that annotations
    /// actually get used in.
    /// </summary>
    private static readonly string[] ToolColors =
    [
        "#000000", "#5A5A5A", "#9A9A9A", "#FFFFFF", "#8E1B4B", "#E22222",
        "#F5761A", "#F5A623", "#FFD200", "#EEF23C", "#7ED321", "#1E9E4A",
        "#12A594", "#1BA1E2", "#2266DD", "#5B2FD1", "#9B51E0", "#C86DD7",
    ];

    private sealed class ToolPanel
    {
        public required Flyout Flyout { get; init; }
        public required ToggleButton Anchor { get; init; }
        public required List<(string Hex, Border Ring)> Swatches { get; init; }
        public Border? Preview { get; set; }
        public Slider? Size { get; set; }
        public Slider? Opacity { get; set; }
    }

    private readonly Dictionary<AnnotationTool, ToolPanel> _toolPanels = [];

    /// <summary>True while any tool's options panel is open (used by the Esc ladder).</summary>
    private bool IsToolOptionsOpen => _toolPanels.Values.Any(p => p.Flyout.IsOpen);

    private void HideToolOptions()
    {
        foreach (var panel in _toolPanels.Values)
        {
            panel.Flyout.Hide();
        }
    }

    /// <summary>Opens a tool's options directly beneath its own button.</summary>
    private void ShowToolOptions(AnnotationTool tool)
    {
        // The note and eraser have nothing to configure; opening an empty panel
        // for them would just be a flash of chrome.
        if (tool is AnnotationTool.Note or AnnotationTool.Eraser)
        {
            return;
        }

        if (!_toolPanels.TryGetValue(tool, out var panel))
        {
            panel = BuildToolPanel(tool);
            _toolPanels[tool] = panel;
        }
        panel.Flyout.ShowAt(panel.Anchor);
    }

    private ToolStyle StyleFor(AnnotationTool tool)
    {
        _state.Settings.MigrateToolStyles();
        return tool == AnnotationTool.Highlighter
            ? _state.Settings.Highlighter!
            : _state.Settings.Pen!;
    }

    private ToolPanel BuildToolPanel(AnnotationTool tool)
    {
        var style = StyleFor(tool);
        bool isMarkup = tool == AnnotationTool.Highlighter;

        var anchor = tool switch
        {
            AnnotationTool.Pen => PenToolButton,
            AnnotationTool.Highlighter => HighlighterToolButton,
            AnnotationTool.Signature => SignToolButton,
            _ => PenToolButton,
        };

        var root = new StackPanel { Spacing = 10, MinWidth = 240 };
        var caption = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var secondary = (Style)Application.Current.Resources["SecondaryTextStyle"];

        var panel = new ToolPanel { Flyout = new Flyout(), Anchor = anchor, Swatches = [] };

        // ---- Signature has its own panel entirely ----
        if (tool == AnnotationTool.Signature)
        {
            root.Children.Add(new TextBlock { Text = "Signature", Style = caption });

            // Saved signatures are filled in asynchronously (decoding PNGs) but
            // the panel itself is built once, so keep a container to refill.
            var list = new StackPanel { Spacing = 6 };
            root.Children.Add(list);
            _signatureList = list;

            root.Children.Add(new TextBlock
            {
                Text = "Click to place at the last size, or drag to draw the box.",
                Style = secondary,
                TextWrapping = TextWrapping.Wrap,
            });

            // "Draw new" since v0.5.0, but the dialog behind it now draws, types
            // or imports, so the button should not name only one of the three.
            var draw = new Button
            {
                Content = "New signature…",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            draw.Click += async (_, _) =>
            {
                panel.Flyout.Hide();
                await ShowSignatureDialogAsync();
            };
            root.Children.Add(draw);

            var import = new Button
            {
                Content = "Import image…",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTipService.SetToolTip(import, "Use a photo or scan of your signature; the paper is removed for you");
            import.Click += async (_, _) =>
            {
                panel.Flyout.Hide();
                await ShowSignatureDialogAsync(startWithImport: true);
            };
            root.Children.Add(import);

            panel.Flyout.Content = root;
            panel.Flyout.Placement = FlyoutPlacementMode.Bottom;
            panel.Flyout.Opened += (_, _) => _ = RefreshSignatureListAsync();
            return panel;
        }

        // ---- Colours: a 6-column grid, like the reference ----
        root.Children.Add(new TextBlock { Text = "Colour", Style = caption });

        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int c = 0; c < 6; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        for (int r = 0; r < (ToolColors.Length + 5) / 6; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < ToolColors.Length; i++)
        {
            string hex = ToolColors[i];

            // A ring marks the selection. It's an outline rather than a tick so
            // it reads on both a black and a white swatch.
            var ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    new Windows.UI.ViewManagement.UISettings().GetColorValue(
                        Windows.UI.ViewManagement.UIColorType.Accent)),
                Visibility = SameColor(style.Color, hex) ? Visibility.Visible : Visibility.Collapsed,
            };
            // The colour lives in the button's CONTENT, not its Background:
            // Button's hover/pressed visual states override Background, which
            // made the swatch turn white under the cursor.
            var dot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(HexToColor(hex)),
                // A hairline keeps white (and near-white) swatches visible.
                BorderThickness = new Thickness(1),
                BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var cell = new Grid();
            cell.Children.Add(dot);
            cell.Children.Add(ring);

            var swatch = new Button
            {
                Style = (Style)Application.Current.Resources["InkSwatchButtonStyle"],
                Content = cell,
            };
            swatch.Click += (_, _) =>
            {
                StyleFor(tool).Color = hex;
                _store.Save(_state);
                ApplyToolStylesToAll(tool);
            };
            panel.Swatches.Add((hex, ring));

            Grid.SetColumn(swatch, i % 6);
            Grid.SetRow(swatch, i / 6);
            grid.Children.Add(swatch);
        }
        root.Children.Add(grid);

        // ---- Markup kind (the highlighter also does underline / strikeout) ----
        if (isMarkup)
        {
            root.Children.Add(new TextBlock { Text = "Style", Style = caption });
            var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var (label, value) in new[] { ("Highlight", "Highlight"), ("Underline", "Underline"), ("Strikeout", "Strikeout") })
            {
                var button = new ToggleButton
                {
                    Content = label,
                    IsChecked = string.Equals(style.Markup, value, StringComparison.OrdinalIgnoreCase),
                };
                button.Click += (s, _) =>
                {
                    StyleFor(tool).Markup = value;
                    _store.Save(_state);
                    // Mutually exclusive: re-check only the chosen one.
                    foreach (var other in kinds.Children.OfType<ToggleButton>())
                    {
                        other.IsChecked = ReferenceEquals(other, s);
                    }
                    ApplyToolStylesToAll(tool);
                };
                kinds.Children.Add(button);
            }
            root.Children.Add(kinds);
        }

        // ---- Size, with a live stroke preview above the slider ----
        if (!isMarkup)
        {
            root.Children.Add(new TextBlock { Text = "Size", Style = caption });

            var preview = new Border
            {
                Height = Math.Max(2, style.Size * 2),
                CornerRadius = new CornerRadius(style.Size),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(HexToColor(style.Color)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(4, 0, 4, 0),
            };
            root.Children.Add(new Grid { Height = 24, Children = { preview } });
            panel.Preview = preview;

            var size = new Slider { Minimum = 1, Maximum = 12, StepFrequency = 0.5, Value = style.Size };
            size.ValueChanged += (_, e) =>
            {
                StyleFor(tool).Size = e.NewValue;
                _store.Save(_state);
                ApplyToolStylesToAll(tool);
            };
            root.Children.Add(size);
            panel.Size = size;
        }

        // ---- Opacity (the reference has this on every drawing tool) ----
        root.Children.Add(new TextBlock { Text = "Opacity", Style = caption });
        var opacity = new Slider { Minimum = 10, Maximum = 100, StepFrequency = 5, Value = style.Opacity * 100 };
        opacity.ValueChanged += (_, e) =>
        {
            StyleFor(tool).Opacity = e.NewValue / 100.0;
            _store.Save(_state);
            ApplyToolStylesToAll(tool);
        };
        root.Children.Add(opacity);
        panel.Opacity = opacity;

        root.Children.Add(new Border { Style = (Style)Application.Current.Resources["FlyoutSeparatorStyle"] });

        var stop = new Button
        {
            Content = "Done",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        ToolTipService.SetToolTip(stop, "Esc");
        stop.Click += (_, _) =>
        {
            SetActiveTool(AnnotationTool.None);
            panel.Flyout.Hide();
        };
        root.Children.Add(stop);

        panel.Flyout.Content = root;
        panel.Flyout.Placement = FlyoutPlacementMode.Bottom;
        return panel;
    }

    private static bool SameColor(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pushes the current styles to every open tab and re-syncs the panel's own
    /// affordances. Deliberately mutates the cached elements rather than
    /// rebuilding — a rebuild would close the open flyout mid-interaction.
    /// </summary>
    private void ApplyToolStylesToAll(AnnotationTool? changed = null)
    {
        _state.Settings.MigrateToolStyles();
        var pen = _state.Settings.Pen!;
        var markup = _state.Settings.Highlighter!;

        foreach (var view in AllDocumentViews())
        {
            view.Viewer.SetInkStyle(pen.Color, pen.Size);
            view.Viewer.SetMarkupStyle(ParseMarkupKind(markup.Markup), markup.Color, markup.Opacity);
        }

        if (changed is { } tool && _toolPanels.TryGetValue(tool, out var panel))
        {
            var style = StyleFor(tool);
            foreach (var (hex, ring) in panel.Swatches)
            {
                ring.Visibility = SameColor(style.Color, hex) ? Visibility.Visible : Visibility.Collapsed;
            }
            if (panel.Preview is { } preview)
            {
                preview.Height = Math.Max(2, style.Size * 2);
                preview.CornerRadius = new CornerRadius(style.Size);
                preview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(HexToColor(style.Color));
            }
        }
    }

    /// <summary>Gives a freshly-opened tab the current tool styles.</summary>
    private void SeedToolStyles(PdfViewer viewer)
    {
        _state.Settings.MigrateToolStyles();
        var pen = _state.Settings.Pen!;
        var markup = _state.Settings.Highlighter!;
        viewer.SetInkStyle(pen.Color, pen.Size);
        viewer.SetMarkupStyle(ParseMarkupKind(markup.Markup), markup.Color, markup.Opacity);
    }

    private readonly SignatureStore _signatures = new();
    private StackPanel? _signatureList;

    /// <summary>Opens the capture pad; a saved signature is armed immediately.</summary>
    /// <param name="startWithImport">Run the file picker as soon as the dialog is up.</param>
    private async Task ShowSignatureDialogAsync(bool startWithImport = false)
    {
        var pad = new SignaturePad { RemoveBackground = _state.Settings.SignatureRemoveBackground };
        var dialog = new ContentDialog
        {
            Title = "Add a signature",
            Content = pad,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (startWithImport)
        {
            // From Opened, not before ShowAsync — the picker needs the dialog
            // already on screen behind it or it opens against a bare window.
            // StartImportAsync swallows its own exceptions, which matters here:
            // this is async void, so anything escaping would kill the process.
            dialog.Opened += async (_, _) => await pad.StartImportAsync();
        }

        var result = await ShowDialogAsync(dialog);

        // Read after the dialog closes rather than pushed while it was open:
        // ShowNotice renders an InfoBar inside DocumentView, which sits behind
        // the modal dialog, so raising it any earlier shows the user nothing.
        if (pad.LastFailure is { } failure)
        {
            ShowError(failure);
        }
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        // Only when the user actually touched the checkbox. The pad also
        // unticks it by itself for an image that has no paper to remove, and
        // saving that as the default would turn the feature off for good.
        if (pad.RemoveBackgroundChosenByUser)
        {
            _state.Settings.SignatureRemoveBackground = pad.RemoveBackground;
            _store.Save(_state);
        }

        if (pad.Rasterize() is not { } image)
        {
            ShowNotice("Nothing was drawn, so there was no signature to save.", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            await _signatures.SaveAsync(image.Bgra, image.Width, image.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"Could not save the signature: {ex.Message}");
            return;
        }

        ArmSignature(image.Bgra, image.Width, image.Height);
    }

    /// <summary>Loads saved signatures into the Sign flyout as clickable previews.</summary>
    private async Task RefreshSignatureListAsync()
    {
        if (_signatureList is not { } list)
        {
            return;
        }

        // Capped for the same reason the import path is: the on-page hover ghost
        // hands this buffer to CanvasBitmap inside a CanvasVirtualControl
        // session, where anything past MaxSingleTilePx silently draws nothing.
        // Signatures saved by earlier builds are still on disk at full
        // resolution, so the cap has to be here too, not only at import.
        var saved = await _signatures.ListAsync(TileMath.MaxSingleTilePx);
        list.Children.Clear();

        if (saved.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No saved signatures yet.",
                Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            });
            return;
        }

        foreach (var signature in saved)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var preview = new Image
            {
                Source = ToBitmapSource(signature),
                Height = 40,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var pick = new Button
            {
                Content = preview,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
            };
            ToolTipService.SetToolTip(pick, "Use this signature");
            pick.Click += (_, _) =>
            {
                ArmSignature(signature.Bgra, signature.Width, signature.Height);
                HideToolOptions(); // get the panel off the page so it can be placed
            };

            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
            ToolTipService.SetToolTip(remove, "Delete this signature");
            remove.Click += async (_, _) =>
            {
                _signatures.Delete(signature.Path);
                await RefreshSignatureListAsync();
            };
            Grid.SetColumn(remove, 1);

            row.Children.Add(pick);
            row.Children.Add(remove);
            list.Children.Add(row);
        }
    }

    /// <summary>Arms a signature on every open tab so switching tabs keeps it.</summary>
    private void ArmSignature(byte[] bgra, int width, int height)
    {
        SetActiveTool(AnnotationTool.Signature);
        foreach (var view in AllDocumentViews())
        {
            view.Viewer.SetPendingSignature(bgra, width, height);
            view.Viewer.SignatureWidthPt = _state.Settings.SignatureWidthPt;
        }
    }

    private static Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap ToBitmapSource(SavedSignature signature)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(signature.Width, signature.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(signature.Bgra, 0, signature.Bgra.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Ctrl+E: arm the pen, or put it away if it already is.</summary>
    private void TogglePenTool()
    {
        bool on = _activeViewer?.ActiveTool == AnnotationTool.Pen;
        SetActiveTool(on ? AnnotationTool.None : AnnotationTool.Pen);
        if (!on)
        {
            ShowToolOptions(AnnotationTool.Pen);
        }
    }

    private static MarkupKind ParseMarkupKind(string value) => value switch
    {
        "Underline" => MarkupKind.Underline,
        "Strikeout" => MarkupKind.Strikeout,
        _ => MarkupKind.Highlight,
    };
}
