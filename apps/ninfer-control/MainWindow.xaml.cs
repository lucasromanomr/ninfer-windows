using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NInferControl;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetInitialSize(widthDip: 1440, heightDip: 1040);
        RootFrame.Navigate(typeof(MainPage));
    }

    private void SetInitialSize(double widthDip, double heightDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        appWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }

    public void Shutdown()
    {
        if (RootFrame.Content is MainPage page)
        {
            page.Shutdown();
        }
    }
}
