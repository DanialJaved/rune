using Microsoft.UI.Xaml;

namespace Folio;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Draw our own title bar so the window chrome matches the app (Preview/Papers style).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }
}
