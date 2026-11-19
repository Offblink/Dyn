using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace Dyn;

internal sealed class Options
{
    public string? Source;
    public string? LogFile;
    public double PreviewSeconds;
    public RectangleF? Crop;      // 归一化裁切区 (x,y,w,h)，用于把动图的一块映射到屏幕
    public bool Quit;
    public bool Help;

    public const string Usage =
        "Dyn —— 用自己的动图当桌面壁纸\n\n" +
        "  Dyn.exe <动画.gif> [选项]\n\n" +
        "  --crop x,y,w,h   只显示动图的这块区域（归一化 0~1，按屏幕长宽比框选）\n" +
        "  --preview <秒>   以普通窗口预览，不接管桌面\n" +
        "  --log <文件>     把运行状态写到文件\n" +
        "  --quit           让正在运行的实例退出\n" +
        "  --help\n\n" +
        "运行中按 Ctrl+Alt+Q 退出。";

    public static bool TryParse(string[] argv, out Options o, out string err)
    {
        o = new Options();
        err = "";
        for (int i = 0; i < argv.Length; i++)
        {
            var a = argv[i];
            switch (a)
            {
                case "--quit": o.Quit = true; continue;
                case "--help":
                case "-h": o.Help = true; continue;
                case "--preview":
                    if (i + 1 >= argv.Length || !double.TryParse(argv[++i], out var sec)) { err = "--preview 需要秒数"; return false; }
                    o.PreviewSeconds = sec;
                    continue;
                case "--crop":
                    if (i + 1 >= argv.Length) { err = "--crop 需要 x,y,w,h"; return false; }
                    var parts = argv[++i].Split(',');
                    if (parts.Length != 4 ||
                        !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cx) ||
                        !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cy) ||
                        !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cw) ||
                        !float.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ch) ||
                        cw <= 0 || ch <= 0)
                    { err = "--crop 格式应为 x,y,w,h（0~1）"; return false; }
                    o.Crop = new RectangleF(cx, cy, cw, ch);
                    continue;
                case "--log":
                    if (i + 1 >= argv.Length) { err = "--log 需要文件路径"; return false; }
                    o.LogFile = argv[++i];
                    continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal)) { err = $"未知参数: {a}"; return false; }
            o.Source = a;
        }
        if (!o.Quit && !o.Help && o.Source is null) { err = "缺少动图路径"; return false; }
        if (o.Source is not null && !File.Exists(o.Source)) { err = $"文件不存在: {o.Source}"; return false; }
        return true;
    }
}

/// <summary>GIF 逐帧解码（GDI+ 负责帧合成），带每帧延时。</summary>
internal sealed class GifPlayer : IDisposable
{
    readonly Image image;
    readonly FrameDimension dimension;
    readonly int[] delays;
    int index;

    public int Width { get; }
    public int Height { get; }

    public GifPlayer(string path)
    {
        image = Image.FromFile(path);
        if (image.FrameDimensionsList.Length == 0) throw new InvalidDataException("不是多帧图片");
        dimension = new FrameDimension(image.FrameDimensionsList[0]);
        Width = image.Width;
        Height = image.Height;

        int count = image.GetFrameCount(dimension);
        delays = new int[Math.Max(1, count)];
        var raw = image.GetPropertyItem(0x5100)?.Value; // FrameDelay，单位 1/100 秒
        for (int i = 0; i < delays.Length; i++)
        {
            int cs = raw is not null && raw.Length >= (i + 1) * 4 ? BitConverter.ToInt32(raw, i * 4) : 10;
            delays[i] = Math.Max(20, cs * 10);
        }
        image.SelectActiveFrame(dimension, 0);
    }

    public int FrameCount => delays.Length;
    public int CurrentDelay => delays[index];
    public Image Current => image;

    public void Next()
    {
        if (delays.Length <= 1) return;
        index = (index + 1) % delays.Length;
        image.SelectActiveFrame(dimension, index);
    }

    public void Dispose() => image.Dispose();
}

internal static class Fit
{
    /// <summary>铺满时按目标比例从图里居中裁一块，避免拉伸变形。</summary>
    public static Rectangle Cover(int srcW, int srcH, int dstW, int dstH)
    {
        double srcAspect = (double)srcW / srcH;
        double dstAspect = (double)dstW / dstH;
        if (srcAspect > dstAspect)
        {
            int w = (int)Math.Round(srcH * dstAspect);
            return new Rectangle((srcW - w) / 2, 0, w, srcH);
        }
        int h = (int)Math.Round(srcW / dstAspect);
        return new Rectangle(0, (srcH - h) / 2, srcW, h);
    }
}

/// <summary>
/// GIF 帧 → D3D11 交换链 → DirectComposition 视觉。
/// 24H2+ 的壁纸层只认 DComp：GDI / UpdateLayeredWindow / SetLayeredWindowAttributes 都不会被合成。
/// 交换链按裁切区尺寸建，缩放交给 DComp 的视觉矩阵（GPU），每帧只上传几百 KB。
/// </summary>
internal sealed class DcompSurface : IDisposable
{
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly IDXGISwapChain1 swap;
    readonly IDCompositionDevice dcomp;
    readonly IDCompositionTarget target;
    readonly IDCompositionVisual visual;
    readonly ID3D11Texture2D staging;
    readonly Bitmap scratch;
    readonly Graphics scratchGraphics;
    readonly byte[] pixels;
    readonly Rectangle crop;
    readonly int w, h;

    public long Presented { get; private set; }

    public DcompSurface(IntPtr hwnd, Rectangle crop, int screenW, int screenH, Action<string> log)
    {
        this.crop = crop;
        w = crop.Width;
        h = crop.Height;

        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out device, out context);

        var dxgi = device.QueryInterface<IDXGIDevice>();
        var factory = dxgi.GetAdapter().GetParent<IDXGIFactory2>();

        swap = factory.CreateSwapChainForComposition(device, new SwapChainDescription1
        {
            Width = (uint)w,
            Height = (uint)h,
            Format = Format.B8G8R8A8_UNorm,
            BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
        });

        dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
        dcomp.CreateTargetForHwnd(hwnd, true, out target);
        visual = dcomp.CreateVisual();
        visual.SetContent(swap);

        float scale = Math.Max((float)screenW / w, (float)screenH / h);
        visual.SetTransform(new Matrix3x2(scale, 0, 0, scale,
            (screenW - w * scale) / 2f, (screenH - h * scale) / 2f));
        target.SetRoot(visual);
        dcomp.Commit();
        log($"dcomp 就绪 交换链={w}x{h} 裁切={crop} 放大={scale:F3}");

        staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        scratch = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        scratchGraphics = Graphics.FromImage(scratch);
        scratchGraphics.CompositingMode = CompositingMode.SourceCopy;
        pixels = new byte[w * h * 4];
    }

    public void Present(Image frame)
    {
        scratchGraphics.DrawImage(frame, new Rectangle(0, 0, w, h), crop, GraphicsUnit.Pixel);

        var data = scratch.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * w * 4, w * 4);
        }
        finally
        {
            scratch.UnlockBits(data);
        }

        unsafe
        {
            fixed (byte* p = pixels)
                context.UpdateSubresource(staging, 0, null, (IntPtr)p, (uint)(w * 4), 0);
        }

        using (var backBuffer = swap.GetBuffer<ID3D11Texture2D>(0))
            context.CopyResource(backBuffer, staging);
        swap.Present(1, PresentFlags.None);
        Presented++;
    }

    public void Dispose()
    {
        visual.Dispose();
        target.Dispose();
        dcomp.Dispose();
        swap.Dispose();
        staging.Dispose();
        context.Dispose();
        device.Dispose();
        scratchGraphics.Dispose();
        scratch.Dispose();
    }
}

/// <summary>挂进桌面的容器窗口：只当句柄用，画面全部由 DComp 提交。</summary>
internal sealed class WallpaperForm : Form
{
    const int WM_HOTKEY = 0x0312;
    const int HotkeyId = 1;

    public WallpaperForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(0, 0, 1, 1);
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;   // 不进任务栏、不进 Alt+Tab
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            Program.Log("收到退出热键");
            Program.RequestQuit();
        }
        base.WndProc(ref m);
    }
}

internal sealed class PreviewForm : Form
{
    readonly GifPlayer player;
    readonly System.Windows.Forms.Timer timer = new();

    public PreviewForm(GifPlayer player, Size size)
    {
        this.player = player;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = size;
        DoubleBuffered = true;
        TopMost = true;
        BackColor = Color.Black;
        timer.Interval = player.CurrentDelay;
        timer.Tick += (_, _) =>
        {
            player.Next();
            timer.Interval = player.CurrentDelay;
            Invalidate();
        };
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var src = Fit.Cover(player.Width, player.Height, ClientSize.Width, ClientSize.Height);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(player.Current, ClientRectangle, src, GraphicsUnit.Pixel);
    }
}

internal static class Program
{
    const string QuitEventName = @"Local\Dyn.Quit";
    const string OwnerMutexName = @"Local\Dyn.Owner";

    static string? logPath;
    static volatile bool quitting;
    static ApplicationContext? context;
    static IntPtr progman, defView, workerW;

    [STAThread]
    static int Main(string[] argv)
    {
        if (!Options.TryParse(argv, out var opt, out var err))
        {
            MessageBox.Show(err + "\n\n" + Options.Usage, "Dyn");
            return 2;
        }
        logPath = opt.LogFile;
        if (logPath is not null) { try { File.WriteAllText(logPath, ""); } catch { } }
        Log($"start pid={Environment.ProcessId} src={opt.Source}");

        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        if (opt.Quit) { quitEvent.Set(); Log("已发送退出信号"); return 0; }
        if (opt.Help) { MessageBox.Show(Options.Usage, "Dyn"); return 0; }

        using var owner = new Mutex(false, OwnerMutexName);
        bool owned = TryTake(owner, 0);
        if (!owned)
        {
            Log("已有实例，先让它退出");
            quitEvent.Set();
            owned = TryTake(owner, 4000);
            if (!owned) { Log("旧实例没退，放弃启动"); return 3; }
        }

        try
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // PerMonitorV2，坐标即物理像素
            Application.EnableVisualStyles();

            GifPlayer player;
            try { player = new GifPlayer(opt.Source!); }
            catch (Exception ex)
            {
                Log($"打开失败: {ex.Message}");
                MessageBox.Show("打不开这个文件: " + ex.Message, "Dyn");
                return 4;
            }
            Log($"动图 {player.Width}x{player.Height} {player.FrameCount} 帧");

            return opt.PreviewSeconds > 0 ? RunPreview(player, opt) : RunWallpaper(player, opt, quitEvent);
        }
        finally
        {
            if (owned) { try { owner.ReleaseMutex(); } catch { } }
        }
    }

    static bool TryTake(Mutex m, int ms)
    {
        try { return m.WaitOne(ms); }
        catch (AbandonedMutexException) { return true; } // 上个实例被杀掉，接管
    }

    static int RunPreview(GifPlayer player, Options opt)
    {
        var screen = Screen.PrimaryScreen!.WorkingArea;
        double scale = Math.Min(1.0, Math.Min(screen.Width * 0.8 / player.Width, screen.Height * 0.8 / player.Height));
        var form = new PreviewForm(player, new Size((int)(player.Width * scale), (int)(player.Height * scale)));
        var killer = new System.Windows.Forms.Timer { Interval = (int)(opt.PreviewSeconds * 1000) };
        killer.Tick += (_, _) => { killer.Stop(); form.Close(); };
        killer.Start();
        Log($"预览 {opt.PreviewSeconds}s");
        Application.Run(form);
        player.Dispose();
        Log("exit");
        return 0;
    }

    static int RunWallpaper(GifPlayer player, Options opt, EventWaitHandle quitEvent)
    {
        int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        Log($"虚拟桌面 {vw}x{vh}");

        var form = new WallpaperForm();
        form.Show();
        var hwnd = form.Handle;

        if (!Native.RegisterHotKey(hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT, 0x51))
            Log("热键 Ctrl+Alt+Q 注册失败");

        AttachToDesktop(hwnd, vw, vh);

        var crop = ResolveCrop(player, opt, vw, vh);
        Log($"裁切区 {crop} （动图 {player.Width}x{player.Height}）");

        DcompSurface surface;
        try
        {
            surface = new DcompSurface(hwnd, crop, vw, vh, Log);
        }
        catch (Exception ex)
        {
            Log($"D3D/DComp 初始化失败: {ex.Message}");
            MessageBox.Show("这台机器上初始化 GPU 合成失败: " + ex.Message, "Dyn");
            return 5;
        }

        surface.Present(player.Current);

        var present = new System.Windows.Forms.Timer { Interval = player.CurrentDelay };
        present.Tick += (_, _) =>
        {
            player.Next();
            present.Interval = player.CurrentDelay;
            surface.Present(player.Current);
        };
        present.Start();

        var guard = new System.Windows.Forms.Timer { Interval = 1000 };
        guard.Tick += (_, _) =>
        {
            // explorer 重启、壁纸层被重建、z 序被打乱时重新挂一次
            if (!Native.IsWindow(progman) || Native.GetParent(hwnd) != progman ||
                ZAnchor(hwnd) != defView)
            {
                AttachToDesktop(hwnd, vw, vh);
            }
        };
        guard.Start();

        var waiter = new Thread(() =>
        {
            quitEvent.WaitOne();
            Log("收到 --quit");
            RequestQuit();
        }) { IsBackground = true, Name = "quit-waiter" };
        waiter.Start();

        context = new ApplicationContext();
        Application.Run(context);

        present.Stop();
        guard.Stop();
        surface.Dispose();
        player.Dispose();
        Log($"exit, 共提交 {surface.Presented} 帧");
        return 0;
    }

    /// <summary>把 --crop（归一化）换算成动图像素矩形；没给就用居中铺满的裁切块。</summary>
    static Rectangle ResolveCrop(GifPlayer player, Options opt, int vw, int vh)
    {
        if (opt.Crop is not { } c) return Fit.Cover(player.Width, player.Height, vw, vh);

        int x = (int)Math.Round(c.X * player.Width);
        int y = (int)Math.Round(c.Y * player.Height);
        int w = Math.Max(1, (int)Math.Round(c.Width * player.Width));
        int h = Math.Max(1, (int)Math.Round(c.Height * player.Height));
        x = Math.Clamp(x, 0, player.Width - 1);
        y = Math.Clamp(y, 0, player.Height - 1);
        w = Math.Min(w, player.Width - x);
        h = Math.Min(h, player.Height - y);
        return new Rectangle(x, y, w, h);
    }

    /// <summary>当前 z 序里紧挨在 hwnd 上面的那个兄弟窗口。</summary>
    static IntPtr ZAnchor(IntPtr hwnd) => Native.GetWindow(hwnd, Native.GW_HWNDPREV);

    /// <summary>
    /// 挂载：窗口改成 WS_CHILD 挂进 Progman，并插到 SHELLDLL_DefView 正下方 ——
    /// 即「图标层之下、壁纸 WorkerW 之上」。图标照常在上层显示和点击。
    /// WorkerW 不存在时必须催生（0x052C），否则 Progman 自己的合成层会盖住一切子窗口。
    /// </summary>
    static bool AttachToDesktop(IntPtr hwnd, int w, int h)
    {
        progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) { Log("Progman 不存在（explorer 没起？）"); return false; }

        defView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        workerW = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (workerW == IntPtr.Zero)
        {
            Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 2000, out _);
            Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 2000, out _);
            System.Threading.Thread.Sleep(200);
            workerW = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
            Log($"催生壁纸层 WorkerW=0x{workerW:X}");
        }

        var style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
        Native.SetWindowLong(hwnd, Native.GWL_STYLE, (style & ~Native.WS_POPUP) | Native.WS_CHILD);
        Native.SetParent(hwnd, progman);
        Native.SetWindowPos(hwnd, defView, 0, 0, w, h, Native.SWP_NOACTIVATE);

        Log($"attach hwnd=0x{hwnd:X} parent=0x{Native.GetParent(hwnd):X} defview=0x{defView:X} " +
            $"workerw=0x{workerW:X} {w}x{h}");
        return true;
    }

    public static void RequestQuit()
    {
        if (quitting) return;
        quitting = true;
        if (context is not null) context.ExitThread();
        Application.Exit();
    }

    public static void Log(string s)
    {
        if (logPath is null) return;
        try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); } catch { }
    }
}
