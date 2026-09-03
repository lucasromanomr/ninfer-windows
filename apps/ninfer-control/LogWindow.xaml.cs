using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NInferControl;

public sealed partial class LogWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public LogWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetInitialSize(widthDip: 1080, heightDip: 720);
    }

    public void SetLog(string text)
    {
        LogTextBox.Text = text;
        LogTextBox.Select(text.Length, 0);
    }

    private void SetInitialSize(double widthDip, double heightDip)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        appWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Text = string.Empty;
    }
}
