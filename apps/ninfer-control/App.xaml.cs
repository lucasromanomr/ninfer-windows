using Microsoft.UI.Xaml;

namespace NInferControl;

public partial class App : Application
{
    private Window? _window;
    public static Window MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        _window.Closed += (_, _) => ((MainWindow)_window).Shutdown();
        _window.Activate();
    }
}
