using Microsoft.UI.Xaml;

namespace Folio;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Activate();

        // Support "Folio.exe <file.pdf> [--page N] [--zoom Z]" — the file path
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
