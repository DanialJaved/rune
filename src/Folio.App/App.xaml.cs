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

        // Support "Folio.exe <file.pdf>" — how Explorer launches the default
        // handler once file association lands (M6).
        string[] commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1 && File.Exists(commandLine[1]))
        {
            await window.LoadDocumentAsync(commandLine[1]);
        }
    }
}
