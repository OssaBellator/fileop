using Microsoft.UI.Xaml;

namespace FileOp.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.InitializeFilesFeature();
        _window = window;
        window.Activate();
    }
}
