using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Dyn;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        App.Main = this;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));

        // Window 没有 Width/Height，且 AppWindow.Resize 收物理像素：按窗口 DPI 换算，
        // 并且必须等首轮布局之后再改，否则会被模板的默认尺寸覆盖。
        DispatcherQueue.TryEnqueue(() =>
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            if (scale <= 0) scale = 1.0;
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(900 * scale), (int)(660 * scale)));
        });
    }
}
