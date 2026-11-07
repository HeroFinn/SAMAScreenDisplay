using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SamaDisplay.Core;
using SamaDisplay.Core.Engine;
using SamaDisplay.Core.Ipc;
using SamaDisplay.Core.Rendering;
using WinForms = System.Windows.Forms;

namespace SamaDisplay.App;

public partial class MainWindow : Window
{
    private readonly IpcClient _ipc = new();
    private readonly DashboardRenderer _renderer = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _brightnessTimer;
    private readonly DispatcherTimer _intervalTimer;
    private DisplayEngine? _localEngine;
    private AppSettings _localSettings;
    private StatusDto? _status;
    private WinForms.NotifyIcon? _tray;
    private bool _initialized;
    private bool _polling;
    private bool _suppress;
    private bool _reallyExit;
    private bool _serviceRunning;
    private DateTime _lastServiceCheck = DateTime.MinValue;
    private string _theme = "Dark";
    private DateTime _lastPreview = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        Logger.Configure(Path.Combine(AppPaths.UserDataDir, "ui.log"));
        _localSettings = AppSettings.Load(AppPaths.UserSettings);
        _theme = _localSettings.Theme;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _brightnessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _brightnessTimer.Tick += BrightnessTimer_Tick;
        _intervalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _intervalTimer.Tick += IntervalTimer_Tick;

        SetupTray();
        Closing += Window_Closing;
        Loaded += Window_Loaded;

        _initialized = true;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        StartMinimizedCheck.IsChecked = _localSettings.StartMinimized;
        FahrenheitCheck.IsChecked = _localSettings.Fahrenheit;
        ApplyThemeRadio(_theme);
        _timer.Start();
        _ = RefreshServiceStateAsync();
        if (_localSettings.StartMinimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "SAMA Display"
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => ShowFromTray());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("启动画面", null, async (_, _) => await SendAsync(new IpcRequest { Cmd = "start" }));
        menu.Items.Add("停止画面", null, async (_, _) => await SendAsync(new IpcRequest { Cmd = "stop" }));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _localSettings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _localSettings.Fahrenheit = FahrenheitCheck.IsChecked == true;
        _localSettings.Theme = _theme;
        _localSettings.Save(AppPaths.UserSettings);
        _tray?.Dispose();
        _tray = null;
        Close();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if ((DateTime.Now - _lastServiceCheck).TotalSeconds >= 5)
            {
                _lastServiceCheck = DateTime.Now;
                _serviceRunning = IsServiceRunning();
            }

            if (_serviceRunning)
            {
                // The service owns COM9; never keep a fallback engine alive.
                DisposeLocalEngine();
                var resp = await _ipc.StatusAsync(4000);
                if (resp is { Ok: true, Status: not null })
                {
                    DisposeLocalEngine();
                    _status = resp.Status;
                    ModeText.Text = "服务模式 · 后台运行";
                }
                else
                {
                    ModeText.Text = "服务运行中 · 正在连接";
                    return;
                }
            }
            else
            {
                EnsureLocalEngine();
                _status = _localEngine?.GetStatus();
                ModeText.Text = "本地模式 · 界面内运行";
            }

            if (_status != null) ApplyStatus(_status);
        }
        catch { }
        finally { _polling = false; }
    }

    private void EnsureLocalEngine()
    {
        if (_localEngine != null) return;
        try
        {
            _localEngine = new DisplayEngine(AppPaths.UserSettings, false);
            _localEngine.Start();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "local engine");
        }
    }

    private void DisposeLocalEngine()
    {
        try { _localEngine?.Dispose(); } catch { }
        _localEngine = null;
    }

    private void ApplyStatus(StatusDto s)
    {
        var online = s.Connected;
        StatusDot.Fill = online ? (System.Windows.Media.Brush)FindResource("Good") : (System.Windows.Media.Brush)FindResource("Bad");
        SideDot.Fill = StatusDot.Fill;
        TitleStatusText.Text = online ? "已连接" : (s.Displaying ? "连接中…" : "已暂停");
        SidePortText.Text = "端口 " + (s.Port ?? "--");
        SideVersionText.Text = "固件 " + (string.IsNullOrWhiteSpace(s.Version) ? "--" : s.Version);
        SideDeviceText.Text = "SAMA 3.4\" LCD";
        BtnToggle.Content = s.Displaying ? "停止显示" : "启动显示";
        BtnStop.IsEnabled = s.Displaying;
        FramesText.Text = $"已发送 {s.FramesSent} 帧";
        FrameMsText.Text = s.LastFrameMs > 0 ? $"{s.LastFrameMs:0} ms/帧" : "-- ms";
        AboutStatusText.Text = $"{ModeText.Text} · 运行 {s.ServiceStarted:HH:mm:ss} 起 · 最后错误：{(string.IsNullOrWhiteSpace(s.LastError) ? "无" : s.LastError)}";

        var sn = s.Sensors;
        CpuNameText.Text = sn.CpuName;
        GpuNameText.Text = sn.GpuName;
        RamNameText.Text = $"{sn.HostName} · {sn.Uptime.Days}天 {sn.Uptime.Hours}时";

        CpuLoadText.Text = FmtPct(sn.CpuLoad);
        CpuTempText.Text = FmtTemp(sn.CpuTemp, FahrenheitCheck.IsChecked == true);
        CpuClockText.Text = sn.CpuClockMhz is > 0 ? $"{sn.CpuClockMhz / 1000.0:0.0} GHz" : "--";
        CpuPowerText.Text = sn.CpuPowerW is > 0 ? $"{sn.CpuPowerW:0} W" : "--";
        CpuFanText.Text = sn.CpuFanRpm is > 0 ? $"{sn.CpuFanRpm:0} RPM" : "--";
        CpuBar.Value = Math.Clamp(sn.CpuLoad ?? 0, 0, 100);

        GpuLoadText.Text = FmtPct(sn.GpuLoad);
        GpuTempText.Text = FmtTemp(sn.GpuTemp, FahrenheitCheck.IsChecked == true);
        GpuClockText.Text = sn.GpuClockMhz is > 0 ? $"{sn.GpuClockMhz:0} MHz" : "--";
        GpuFanText.Text = sn.GpuFanRpm is > 0 ? $"{sn.GpuFanRpm:0} RPM" : "--";
        GpuVramText.Text = sn.GpuMemUsedMb is > 0
            ? $"{sn.GpuMemUsedMb / 1024.0:0.0}/{Math.Max(sn.GpuMemTotalMb ?? 0, sn.GpuMemUsedMb ?? 0) / 1024.0:0.0}G"
            : "--";
        GpuBar.Value = Math.Clamp(sn.GpuLoad ?? 0, 0, 100);

        var ramPct = sn.RamPercent ?? 0;
        RamPercentText.Text = FmtPct(sn.RamPercent);
        RamUsedText.Text = sn.RamUsedGb is > 0 ? $"{sn.RamUsedGb:0.0} GB" : "--";
        RamTotalText.Text = sn.RamTotalGb is > 0 ? $"{sn.RamTotalGb:0.0} GB" : "--";
        RamFreeText.Text = sn.RamTotalGb is > 0 && sn.RamUsedGb is > 0 ? $"{Math.Max(0, sn.RamTotalGb.Value - sn.RamUsedGb.Value):0.0} GB" : "--";
        RamBar.Value = Math.Clamp(ramPct, 0, 100);

        _suppress = true;
        try
        {
            if (Math.Abs(BrightnessSlider.Value - s.Brightness) > 0.5) BrightnessSlider.Value = s.Brightness;
            if (Math.Abs(BrightnessSlider2.Value - s.Brightness) > 0.5) BrightnessSlider2.Value = s.Brightness;
            BrightnessValueText.Text = s.Brightness.ToString();
            BrightnessValue2Text.Text = s.Brightness.ToString();
            if (Math.Abs(IntervalSlider.Value - s.IntervalSeconds) > 0.5) IntervalSlider.Value = s.IntervalSeconds;
            if (Math.Abs(IntervalSlider2.Value - s.IntervalSeconds) > 0.5) IntervalSlider2.Value = s.IntervalSeconds;
            IntervalValueText.Text = $"{s.IntervalSeconds} 秒";
            IntervalValue2Text.Text = $"{s.IntervalSeconds} 秒";
            if (!string.IsNullOrWhiteSpace(s.Theme) && s.Theme != _theme)
            {
                _theme = s.Theme;
                ApplyThemeRadio(_theme);
            }
        }
        finally { _suppress = false; }

        if ((DateTime.Now - _lastPreview).TotalMilliseconds > 1200)
        {
            _lastPreview = DateTime.Now;
            RefreshPreview();
        }
    }

    private void RefreshPreview()
    {
        try
        {
            var sn = _status?.Sensors ?? new SensorSnapshot();
            var cfg = new AppSettings
            {
                Theme = _theme,
                IntervalSeconds = (int)IntervalSlider.Value,
                Fahrenheit = FahrenheitCheck.IsChecked == true,
                JpegQuality = (int)QualitySlider.Value
            };
            using var bmp = _renderer.RenderBitmap(sn, cfg);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            PreviewImage.Source = img;
            PreviewImageLarge.Source = img;
        }
        catch { }
    }

    private static string FmtPct(double? v) => v is null ? "--%" : $"{v:0}%";
    private static string FmtTemp(double? c, bool f)
    {
        if (c is not > 0) return "--°";
        var v = f ? c.Value * 9 / 5 + 32 : c.Value;
        return $"{v:0}°";
    }

    private void ApplyThemeRadio(string theme)
    {
        _suppress = true;
        try
        {
            ThemeDark.IsChecked = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
            ThemeNeon.IsChecked = theme.Equals("Neon", StringComparison.OrdinalIgnoreCase);
            ThemeMinimal.IsChecked = theme.Equals("Minimal", StringComparison.OrdinalIgnoreCase)
                || theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
        }
        finally { _suppress = false; }
    }

    private async Task SendAsync(IpcRequest req)
    {
        var resp = await _ipc.SendAsync(req, 15000);
        if (resp is { Ok: true, Status: not null })
        {
            _status = resp.Status;
            ApplyStatus(resp.Status);
        }
    }

    // ---------- title bar ----------
    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMax_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _localSettings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _localSettings.Fahrenheit = FahrenheitCheck.IsChecked == true;
        _localSettings.Theme = _theme;
        _localSettings.Save(AppPaths.UserSettings);

        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            _tray?.ShowBalloonTip(1500, "SAMA Display", "程序已最小化到托盘，显示服务仍在运行。", WinForms.ToolTipIcon.Info);
        }
        else
        {
            _timer.Stop();
            _localEngine?.Dispose();
            _tray?.Dispose();
        }
    }

    // ---------- navigation ----------
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || PageDashboard == null) return;
        var dash = ReferenceEquals(sender, NavDashboard);
        var display = ReferenceEquals(sender, NavDisplay);
        var settings = ReferenceEquals(sender, NavSettings);
        PageDashboard.Visibility = dash ? Visibility.Visible : Visibility.Collapsed;
        PageDisplay.Visibility = display ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- actions ----------
    private async void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        var start = _status?.Displaying != true;
        await SendAsync(new IpcRequest { Cmd = start ? "start" : "stop" });
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e) =>
        await SendAsync(new IpcRequest { Cmd = "stop" });

    private async void BtnPlayVideo_Click(object sender, RoutedEventArgs e)
    {
        var path = VideoPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return;
        await SendAsync(new IpcRequest { Cmd = "video", Text = path });
    }

    private async void BtnBackToImage_Click(object sender, RoutedEventArgs e)
    {
        await SendAsync(new IpcRequest { Cmd = "video", Text = "" });
        await SendAsync(new IpcRequest { Cmd = "start" });
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _suppress) return;
        BrightnessValueText.Text = ((int)e.NewValue).ToString();
        _suppress = true;
        try { BrightnessSlider2.Value = e.NewValue; } finally { _suppress = false; }
        _brightnessTimer.Stop();
        _brightnessTimer.Start();
    }

    private void BrightnessSlider2_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _suppress) return;
        BrightnessValue2Text.Text = ((int)e.NewValue).ToString();
        _suppress = true;
        try { BrightnessSlider.Value = e.NewValue; } finally { _suppress = false; }
        _brightnessTimer.Stop();
        _brightnessTimer.Start();
    }

    private async void BrightnessTimer_Tick(object? sender, EventArgs e)
    {
        _brightnessTimer.Stop();
        var value = (int)BrightnessSlider.Value;
        BrightnessValueText.Text = value.ToString();
        BrightnessValue2Text.Text = value.ToString();
        await SendAsync(new IpcRequest { Cmd = "brightness", Value = value });
        RefreshPreview();
    }

    private void IntervalSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _suppress) return;
        IntervalValueText.Text = $"{(int)e.NewValue} 秒";
        _suppress = true;
        try { IntervalSlider2.Value = e.NewValue; } finally { _suppress = false; }
        _intervalTimer.Stop();
        _intervalTimer.Start();
    }

    private void IntervalSlider2_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _suppress) return;
        IntervalValue2Text.Text = $"{(int)e.NewValue} 秒";
        _suppress = true;
        try { IntervalSlider.Value = e.NewValue; } finally { _suppress = false; }
        _intervalTimer.Stop();
        _intervalTimer.Start();
    }

    private async void IntervalTimer_Tick(object? sender, EventArgs e)
    {
        _intervalTimer.Stop();
        await SendAsync(new IpcRequest { Cmd = "interval", Value = (int)IntervalSlider.Value });
        RefreshPreview();
    }

    private async void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppress) return;
        _theme = ReferenceEquals(sender, ThemeNeon) ? "Neon"
            : ReferenceEquals(sender, ThemeMinimal) ? "Minimal" : "Dark";
        await SendAsync(new IpcRequest { Cmd = "theme", Text = _theme });
        RefreshPreview();
    }

    private async void QualitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || _suppress) return;
        QualityValueText.Text = ((int)e.NewValue).ToString();
        await SendAsync(new IpcRequest { Cmd = "quality", Value = (int)e.NewValue });
        RefreshPreview();
    }

    private async void Rotate_Checked(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppress) return;
        var value = ReferenceEquals(sender, Rotate90) ? 1
            : ReferenceEquals(sender, Rotate180) ? 2
            : ReferenceEquals(sender, Rotate270) ? 3 : 0;
        await SendAsync(new IpcRequest { Cmd = "rotate", Value = value });
    }

    // ---------- service ----------
    private async Task RefreshServiceStateAsync()
    {
        await Task.Yield();
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            var status = sc.Status;
            ServiceStateText.Text = $"服务已安装 · {Translate(status)}";
            ServiceDetailText.Text = $"服务名：{AppPaths.ServiceName} · 启动类型：{sc.StartType}";
            BtnInstallService.Content = status == ServiceControllerStatus.Running ? "重启服务" : "启动服务";
            BtnUninstallService.IsEnabled = true;
        }
        catch
        {
            ServiceStateText.Text = "未检测到服务（当前为本地模式）";
            ServiceDetailText.Text = "安装后会注册为 Windows 服务，开机即静默运行。";
            BtnInstallService.Content = "安装并启动服务";
            BtnUninstallService.IsEnabled = false;
        }
    }

    private static string Translate(ServiceControllerStatus s) => s switch
    {
        ServiceControllerStatus.Running => "运行中",
        ServiceControllerStatus.Stopped => "已停止",
        ServiceControllerStatus.StartPending => "启动中",
        ServiceControllerStatus.StopPending => "停止中",
        _ => s.ToString()
    };

    private static bool IsServiceRunning()
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    private void BtnInstallService_Click(object sender, RoutedEventArgs e)
    {
        var script = FindScript("install-service.ps1");
        if (script == null)
        {
            MessageBox.Show("找不到安装脚本 install-service.ps1。\n请先运行 publish.ps1 生成发布目录。", "SAMA Display",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DisposeLocalEngine();
        RunElevated(script);
        _ = Task.Delay(3500).ContinueWith(_ => Dispatcher.Invoke(() => _ = RefreshServiceStateAsync()));
    }

    private void BtnUninstallService_Click(object sender, RoutedEventArgs e)
    {
        var script = FindScript("uninstall-service.ps1");
        if (script == null)
        {
            MessageBox.Show("找不到卸载脚本 uninstall-service.ps1。", "SAMA Display",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show("确定要卸载后台服务吗？显示会停止，直到重新启动。", "SAMA Display",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RunElevated(script);
        _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.Invoke(() => _ = RefreshServiceStateAsync()));
    }

    private static string? FindScript(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "install", name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static void RunElevated(string script)
    {
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(script)!
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动安装程序失败：" + ex.Message, "SAMA Display", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _localSettings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        _localSettings.Fahrenheit = FahrenheitCheck.IsChecked == true;
        _localSettings.Theme = _theme;
        _localSettings.Save(AppPaths.UserSettings);

        var port = PortBox.Text?.Trim();
        if (port == "自动检测") port = "";
        _ = SendAsync(new IpcRequest { Cmd = "port", Text = port ?? "" });
        _ = SendAsync(new IpcRequest { Cmd = "fahrenheit", Value = FahrenheitCheck.IsChecked == true ? 1 : 0 });
        RefreshPreview();
        MessageBox.Show("设置已保存。", "SAMA Display", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        var path = File.Exists(AppPaths.ServiceLog) ? AppPaths.ServiceLog : Path.Combine(AppPaths.UserDataDir, "ui.log");
        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show("暂无日志文件。", "SAMA Display", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("notepad.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开日志失败：" + ex.Message, "SAMA Display", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
