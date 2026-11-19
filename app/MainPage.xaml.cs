using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Path = System.IO.Path;

namespace Dyn;

public sealed partial class MainPage : Page
{
    // 引擎就是 Dyn.Engine.exe：它自己负责挂进桌面 + DComp 播放，GUI 只负责选文件/框选/启停
    static readonly string EngineExe = Path.Combine(AppContext.BaseDirectory, "Dyn.Engine.exe");
    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dyn", "last.txt");

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    const int SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    string? gif;
    double srcW, srcH;          // 动图原始像素
    Rect? crop;                 // 归一化裁切区 (0~1)
    Point? dragStart;           // 拖动起点（相对 PreviewHost）
    Rect? dragRect;             // 拖动中的框（相对 PreviewHost）
    DispatcherTimer? poll;

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        poll.Tick += (_, _) => RefreshStatus();
        poll.Start();

        var fromArgs = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.EndsWith(".gif", StringComparison.OrdinalIgnoreCase));
        var last = fromArgs ?? (File.Exists(StateFile) ? File.ReadAllText(StateFile).Trim() : null);
        if (!string.IsNullOrWhiteSpace(last) && File.Exists(last)) SetGif(last!);

        if (!File.Exists(EngineExe)) StatusText.Text = $"缺少引擎：{EngineExe}";
        RefreshStatus();
    }

    async void Choose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".gif");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Main!));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        SetGif(file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, file.Path);
    }

    void SetGif(string path)
    {
        gif = path;
        PathText.Text = path;
        ApplyButton.IsEnabled = true;

        crop = null;
        dragRect = null;
        dragStart = null;
        UpdateOverlay(null);

        var bmp = new BitmapImage();
        bmp.ImageOpened += (_, _) =>
        {
            srcW = bmp.PixelWidth;
            srcH = bmp.PixelHeight;
            UpdateOverlay(null);
        };
        bmp.UriSource = new Uri(path);
        Preview.Source = bmp;
    }

    // ── 框选 ────────────────────────────────────────────────

    /// <summary>动图在 PreviewHost 里的实际显示矩形（Stretch=Uniform 会留黑边）。</summary>
    Rect ContentRect()
    {
        double hostW = PreviewHost.ActualWidth, hostH = PreviewHost.ActualHeight;
        if (srcW <= 0 || srcH <= 0 || hostW <= 0 || hostH <= 0) return new Rect(0, 0, hostW, hostH);
        double scale = Math.Min(hostW / srcW, hostH / srcH);
        double w = srcW * scale, h = srcH * scale;
        return new Rect((hostW - w) / 2, (hostH - h) / 2, w, h);
    }

    static double ScreenAspect()
    {
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return w > 0 && h > 0 ? (double)w / h : 16.0 / 9.0;
    }

    void Preview_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Preview.Source is null) return;
        var p = e.GetCurrentPoint(PreviewHost).Position;
        if (!ContentRect().Contains(p)) return;

        dragStart = p;
        dragRect = new Rect(p, p);
        PreviewHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void Preview_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is not { } start) return;

        var p = e.GetCurrentPoint(PreviewHost).Position;
        var c = ContentRect();
        double aspect = ScreenAspect();

        double dx = p.X - start.X, dy = p.Y - start.Y;
        bool right = dx >= 0, down = dy >= 0;

        // 先按鼠标判定以哪条边为准，再按屏幕比例补齐另一边
        double w, h;
        if (Math.Abs(dx) >= Math.Abs(dy) * aspect) { w = Math.Abs(dx); h = w / aspect; }
        else { h = Math.Abs(dy); w = h * aspect; }

        // 顶到图片边就整体缩回来，保证框不越界
        w = Math.Min(w, right ? c.Right - start.X : start.X - c.Left);
        h = Math.Min(h, down ? c.Bottom - start.Y : start.Y - c.Top);
        w = Math.Min(w, h * aspect);
        h = w / aspect;

        dragRect = new Rect(right ? start.X : start.X - w, down ? start.Y : start.Y - h, w, h);
        UpdateOverlay(dragRect);
        e.Handled = true;
    }

    void Preview_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is null) return;
        PreviewHost.ReleasePointerCapture(e.Pointer);
        dragStart = null;

        var c = ContentRect();
        if (dragRect is { } r && r.Width >= 12 && c.Width > 0)
        {
            crop = new Rect(
                Math.Clamp((r.X - c.X) / c.Width, 0, 1),
                Math.Clamp((r.Y - c.Y) / c.Height, 0, 1),
                Math.Clamp(r.Width / c.Width, 0, 1),
                Math.Clamp(r.Height / c.Height, 0, 1));
        }
        dragRect = null;
        UpdateOverlay(null);
        e.Handled = true;
    }

    void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverlay(dragRect);

    void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        crop = null;
        dragRect = null;
        UpdateOverlay(null);
    }

    /// <summary>选区 = 拖动中的框，否则 = 已确定的 crop；都没有就全部隐藏。</summary>
    void UpdateOverlay(Rect? live)
    {
        var host = new Rect(0, 0, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
        Rect? sel = live;
        if (sel is null && crop is { } c)
        {
            var img = ContentRect();
            sel = new Rect(img.X + c.X * img.Width, img.Y + c.Y * img.Height,
                           c.Width * img.Width, c.Height * img.Height);
        }

        if (sel is not { } s || s.Width < 2 || s.Height < 2)
        {
            foreach (var r in new[] { DimTop, DimBottom, DimLeft, DimRight, SelBorder })
                r.Visibility = Visibility.Collapsed;
            HintText.Text = "在预览里拖一块区域，就只显示那块（比例自动锁定屏幕）";
            ResetCropButton.IsEnabled = crop is not null;
            return;
        }

        SetRect(DimTop, new Rect(0, 0, host.Width, s.Y));
        SetRect(DimBottom, new Rect(0, s.Bottom, host.Width, Math.Max(0, host.Height - s.Bottom)));
        SetRect(DimLeft, new Rect(0, s.Y, s.X, s.Height));
        SetRect(DimRight, new Rect(s.Right, s.Y, Math.Max(0, host.Width - s.Right), s.Height));
        SetRect(SelBorder, s);
        HintText.Text = crop is { } cc
            ? $"已框选 {cc.Width * 100:0.#}% × {cc.Height * 100:0.#}%（点「设为桌面壁纸」生效）"
            : "松开鼠标确定选区";
        ResetCropButton.IsEnabled = crop is not null;
    }

    static void SetRect(Rectangle r, Rect rect)
    {
        Canvas.SetLeft(r, rect.X);
        Canvas.SetTop(r, rect.Y);
        r.Width = Math.Max(0, rect.Width);
        r.Height = Math.Max(0, rect.Height);
        r.Visibility = rect.Width > 0.5 && rect.Height > 0.5 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 启停 ────────────────────────────────────────────────

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (gif is null) return;
        if (!File.Exists(EngineExe)) { StatusText.Text = "找不到引擎 Dyn.Engine.exe"; return; }

        var args = $"\"{gif}\"";
        if (crop is { } c)
            args += string.Create(CultureInfo.InvariantCulture,
                $" --crop {c.X:0.####},{c.Y:0.####},{c.Width:0.####},{c.Height:0.####}");

        Launch(args);
        StatusText.Text = "正在应用…";
        RefreshStatus();
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(EngineExe)) return;
        Launch("--quit");
        StatusText.Text = "已停止";
    }

    static void Launch(string args)
    {
        var psi = new ProcessStartInfo(EngineExe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        Process.Start(psi);
    }

    void RefreshStatus()
    {
        if (!File.Exists(EngineExe)) return;
        bool running = Process.GetProcessesByName("Dyn.Engine").Length > 0;
        StatusText.Text = running ? "桌面壁纸运行中（Ctrl+Alt+Q 退出）" : "已停止";
    }
}
