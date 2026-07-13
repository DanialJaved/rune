using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Rune.Controls;

/// <summary>One executable entry in the command palette.</summary>
public sealed record PaletteCommand(string Label, string Shortcut, Action Execute);

/// <summary>
/// Sumatra-style Ctrl+K command palette: type-to-filter command list with a
/// dynamic "Go to page N" entry when the query is a number. The host supplies
/// a fresh command list on every <see cref="Show"/> so enablement is current.
/// </summary>
public sealed partial class CommandPalette : UserControl
{
    private IReadOnlyList<PaletteCommand> _commands = [];
    private Func<int, PaletteCommand?>? _goToPageFactory;

    public bool IsOpen => Visibility == Visibility.Visible;

    public CommandPalette()
    {
        InitializeComponent();
        // Dismiss when focus leaves the palette (click elsewhere, etc.).
        LostFocus += (_, _) =>
        {
            if (IsOpen && FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement fe && !IsChildOfPalette(fe))
            {
                Hide();
            }
        };
    }

    private bool IsChildOfPalette(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current == this)
            {
                return true;
            }
            current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <param name="goToPageFactory">Builds a "Go to page N" command for numeric queries; null disables.</param>
    public void Show(IReadOnlyList<PaletteCommand> commands, Func<int, PaletteCommand?>? goToPageFactory)
    {
        _commands = commands;
        _goToPageFactory = goToPageFactory;
        Input.Text = "";
        Visibility = Visibility.Visible;
        Filter("");
        Input.Focus(FocusState.Programmatic);
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    private void Input_TextChanged(object sender, TextChangedEventArgs e) => Filter(Input.Text);

    private void Filter(string query)
    {
        query = query.Trim();

        var matches = new List<PaletteCommand>();
        if (query.Length > 0 && int.TryParse(query, out int page) && _goToPageFactory?.Invoke(page) is { } goTo)
        {
            matches.Add(goTo);
        }

        // Rank: prefix matches first, then substring, then in-order subsequence.
        matches.AddRange(_commands
            .Select(c => (Command: c, Rank: RankMatch(c.Label, query)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Command.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Command));

        Results.ItemsSource = matches;
        if (matches.Count > 0)
        {
            Results.SelectedIndex = 0;
        }
    }

    private static int RankMatch(string label, string query)
    {
        if (query.Length == 0)
        {
            return 2;
        }
        if (label.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        if (label.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        int qi = 0;
        foreach (char c in label)
        {
            if (qi < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[qi]))
            {
                qi++;
            }
        }
        return qi == query.Length ? 3 : -1;
    }

    private void Input_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Hide();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                RunSelected();
                e.Handled = true;
                break;

            case VirtualKey.Down:
            case VirtualKey.Up:
                int count = (Results.ItemsSource as List<PaletteCommand>)?.Count ?? 0;
                if (count > 0)
                {
                    int delta = e.Key == VirtualKey.Down ? 1 : -1;
                    Results.SelectedIndex = ((Results.SelectedIndex + delta) % count + count) % count;
                    Results.ScrollIntoView(Results.SelectedItem);
                }
                e.Handled = true;
                break;
        }
    }

    private void Results_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteCommand command)
        {
            Hide();
            command.Execute();
        }
    }

    private void RunSelected()
    {
        if (Results.SelectedItem is PaletteCommand command)
        {
            Hide();
            command.Execute();
        }
    }
}
