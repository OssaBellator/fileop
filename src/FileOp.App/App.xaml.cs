using Microsoft.UI.Xaml;

namespace FileOp.App;

public partial class App : Application
{
    private Window? _window;

    internal MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.InitializeStorageSourceIdentityTracking();
        window.InitializeFilesFeature();
        MainWindow = window;
        _window = window;
        window.Activate();
    }
}
