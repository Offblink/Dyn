using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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

    enum DragMode { None, Create, Move }

    string? gif;
    double srcW, srcH;          // 动图原始像素
    Rect? crop;                 // 已确定的裁切区，归一化 0~1
    DragMode mode;
    Point pressPoint;           // 按下点（PreviewHost 坐标）
    Point grabOffset;           // Move 时，按下点相对框左上角的偏移
    Rect? live;                 // 正在拖的框（PreviewHost 坐标）
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
        live = null;
        mode = DragMode.None;
        UpdateOverlay();

        var bmp = new BitmapImage();
        bmp.ImageOpened += (_, _) =>
        {
            srcW = bmp.PixelWidth;
            srcH = bmp.PixelHeight;
            UpdateOverlay();
        };
        bmp.UriSource = new Uri(path);
        Preview.Source = bmp;
    }

    // ── 几何 ────────────────────────────────────────────────

    /// <summary>动图在 PreviewHost 里的实际显示矩形（Stretch=Uniform 会留边）。</summary>
    Rect ContentRect()
    {
        double hostW = PreviewHost.ActualWidth, hostH = PreviewHost.ActualHeight;
        if (srcW <= 0 || srcH <= 0 || hostW <= 0 || hostH <= 0) return new Rect(0, 0, hostW, hostH);
        double scale = Math.Min(hostW / srcW, hostH / srcH);
        double w = srcW * scale, h = srcH * scale;
        return new Rect((hostW - w) / 2, (hostH - h) / 2, w, h);
    }

    Rect CropToHost(Rect c)
    {
        var img = ContentRect();
        return new Rect(img.X + c.X * img.Width, img.Y + c.Y * img.Height,
                        c.Width * img.Width, c.Height * img.Height);
    }

    Rect HostToCrop(Rect r)
    {
        var img = ContentRect();
        return new Rect(
            Math.Clamp((r.X - img.X) / img.Width, 0, 1),
            Math.Clamp((r.Y - img.Y) / img.Height, 0, 1),
            Math.Clamp(r.Width / img.Width, 0, 1),
            Math.Clamp(r.Height / img.Height, 0, 1));
    }

    static double ScreenAspect()
    {
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN), h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return w > 0 && h > 0 ? (double)w / h : 16.0 / 9.0;
    }

    /// <summary>把任意大小的框按屏幕比例校正（保持中心），并收进图片范围内。</summary>
    static Rect FitAspect(Rect r, double aspect, Rect bounds)
    {
        double w = r.Width, h = r.Height;
        if (w / h > aspect) h = w / aspect; else w = h * aspect;
        if (w > bounds.Width) { w = bounds.Width; h = w / aspect; }
        if (h > bounds.Height) { h = bounds.Height; w = h * aspect; }
        double x = Math.Clamp(r.X + r.Width / 2 - w / 2, bounds.X, bounds.Right - w);
        double y = Math.Clamp(r.Y + r.Height / 2 - h / 2, bounds.Y, bounds.Bottom - h);
        return new Rect(x, y, w, h);
    }

    static Rect ClampRect(Rect r, Rect b)
    {
        double w = Math.Min(r.Width, b.Width), h = Math.Min(r.Height, b.Height);
        double x = Math.Clamp(r.X, b.X, b.Right - w);
        double y = Math.Clamp(r.Y, b.Y, b.Bottom - h);
        return new Rect(x, y, w, h);
    }

    // ── 指针 ────────────────────────────────────────────────

    void Preview_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Preview.Source is null) return;
        var p = e.GetCurrentPoint(PreviewHost).Position;
        var content = ContentRect();
        if (!content.Contains(p)) return;

        // 框里按下 → 移动整框；框外按下 → 重新画
        if (crop is { } c)
        {
            var box = CropToHost(c);
            if (box.Contains(p))
            {
                mode = DragMode.Move;
                grabOffset = new Point(p.X - box.X, p.Y - box.Y);
                live = box;
                PreviewHost.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }
        }

        mode = DragMode.Create;
        pressPoint = p;
        live = new Rect(p.X, p.Y, 0, 0);
        PreviewHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    void Preview_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (mode == DragMode.None) return;

        var p = e.GetCurrentPoint(PreviewHost).Position;
        var content = ContentRect();

        if (mode == DragMode.Create)
        {
            // 角严格跟着鼠标走，松手时再按屏幕比例校正
            double x = Math.Min(pressPoint.X, p.X), y = Math.Min(pressPoint.Y, p.Y);
            double w = Math.Abs(p.X - pressPoint.X), h = Math.Abs(p.Y - pressPoint.Y);
            live = ClampRect(new Rect(x, y, w, h), content);
        }
        else
        {
            var target = new Rect(p.X - grabOffset.X, p.Y - grabOffset.Y, live?.Width ?? 0, live?.Height ?? 0);
            live = ClampRect(target, content);
        }
        UpdateOverlay();
        e.Handled = true;
    }

    void Preview_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (mode == DragMode.None) return;
        PreviewHost.ReleasePointerCapture(e.Pointer);

        var content = ContentRect();
        var r = live;
        if (mode == DragMode.Create)
        {
            if (r is { } box && box.Width >= 12 && box.Height >= 12)
                crop = HostToCrop(FitAspect(box, ScreenAspect(), content));
        }
        else if (r is { } moved)
        {
            crop = HostToCrop(moved);
        }

        mode = DragMode.None;
        live = null;
        UpdateOverlay();
        e.Handled = true;
    }

    void PreviewHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverlay();

    void ResetCrop_Click(object sender, RoutedEventArgs e)
    {
        crop = null;
        live = null;
        mode = DragMode.None;
        UpdateOverlay();
    }

    /// <summary>画暗区 + 描边；优先画正在拖的框，其次画已确定的裁切区。</summary>
    void UpdateOverlay()
    {
        var host = new Rect(0, 0, PreviewHost.ActualWidth, PreviewHost.ActualHeight);
        Rect? sel = live ?? (crop is { } c ? CropToHost(c) : null);

        if (sel is not { } s || s.Width < 2 || s.Height < 2)
        {
            foreach (var r in new[] { DimTop, DimBottom, DimLeft, DimRight, SelBorder })
                r.Visibility = Visibility.Collapsed;
            HintText.Text = "在预览里拖出选框；框好后拖框内可移动（松手按屏幕比例校正）";
            ResetCropButton.IsEnabled = crop is not null;
            return;
        }

        SetRect(DimTop, new Rect(0, 0, host.Width, s.Y));
        SetRect(DimBottom, new Rect(0, s.Bottom, host.Width, Math.Max(0, host.Height - s.Bottom)));
        SetRect(DimLeft, new Rect(0, s.Y, s.X, s.Height));
        SetRect(DimRight, new Rect(s.Right, s.Y, Math.Max(0, host.Width - s.Right), s.Height));
        SetRect(SelBorder, s);

        var img = ContentRect();
        HintText.Text = mode switch
        {
            DragMode.Create => $"松开按屏幕比例成形（当前 {s.Width / img.Width * 100:0}% 宽）",
            DragMode.Move => $"移动中：{s.Width / img.Width * 100:0}% × {s.Height / img.Height * 100:0}%",
            _ => $"已框选 {s.Width / img.Width * 100:0.#}% × {s.Height / img.Height * 100:0.#}%（框内拖动可移动）",
        };
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
