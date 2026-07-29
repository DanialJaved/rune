using Microsoft.UI.Xaml;
using Rune.Services;

namespace Rune;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // Safety net. An `async void` handler (every WinUI event handler is one)
        // has no Task to park an exception in, so the runtime rethrows it on the
        // dispatcher and the process dies unless Handled is set here. Log it and
        // keep running — a reader losing an hour of annotations to a transient
        // failure is far worse than a stale error message.
        UnhandledException += (_, e) =>
        {
            ErrorLog.Default.Write("UnhandledException", e.Exception);
            e.Handled = true;
            ReportToUser(e.Exception.Message);
        };

        // The other half: `_ = SomeAsync()` parks a failure in a Task nobody
        // awaits, which is silently swallowed. This surfaces those too.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Default.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Surfaces a background failure in the main window's InfoBar. Never a
    /// dialog: the very bug this net was added for was "a second ContentDialog
    /// was shown", so opening one from the handler could re-trigger it.
    /// </summary>
    private static void ReportToUser(string message)
    {
        try
        {
            (MainWindow as MainWindow)?.ReportBackgroundError(message);
        }
        catch
        {
            // Window may be closing or not built yet — the log already has it.
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        MainWindow = window;

        // Window/taskbar icon (the exe icon comes from ApplicationIcon in the csproj).
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "rune.ico");
        if (File.Exists(icon))
        {
            window.AppWindow.SetIcon(icon);
        }

        window.Activate();

        // Support "Rune.exe <file.pdf> [--page N] [--zoom Z]" — the file path
        // is how Explorer launches the default handler once file association
        // lands (M6); --page/--zoom are for scripted testing.
        string[] commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1 && File.Exists(commandLine[1]))
        {
            int? page = null;
            double? zoom = null;
            for (int i = 2; i < commandLine.Length - 1; i++)
            {
                if (commandLine[i] == "--page" && int.TryParse(commandLine[i + 1], out int p))
                {
                    page = p;
                }
                if (commandLine[i] == "--zoom" && double.TryParse(commandLine[i + 1], out double z))
                {
                    zoom = z;
                }
            }
            await window.LoadDocumentAsync(commandLine[1], page, zoom);
        }
    }
}
