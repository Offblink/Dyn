using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Dyn;

public sealed partial class MainPage : Page
{
    // 引擎就是 Dyn.Engine.exe：它自己负责挂进桌面 + DComp 播放，GUI 只负责选文件/启停
    static readonly string EngineExe = Path.Combine(AppContext.BaseDirectory, "Dyn.Engine.exe");
    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dyn", "last.txt");

    string? gif;
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

        // 命令行 / 上次选择
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
        Preview.Source = new BitmapImage(new Uri(path));
        ApplyButton.IsEnabled = true;
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (gif is null) return;
        if (!File.Exists(EngineExe)) { StatusText.Text = "找不到引擎 Dyn.Engine.exe"; return; }
        Launch($"\"{gif}\"");
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
