using System.IO;
using Microsoft.UI.Xaml;

namespace NInferControl;

public partial class App : Application
{
    private Window? _window;
    public static Window MainWindow { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "NInferControl-crash.log");
                File.WriteAllText(path, DateTime.Now.ToString("O") + Environment.NewLine + e.Exception);
            }
            catch
            {
                // Nothing useful to do if even the crash log cannot be written.
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        _window.Closed += (_, _) => ((MainWindow)_window).Shutdown();
        _window.Activate();
    }
}
