using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SamaDisplay.Core;

namespace SamaDisplay.App;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "SamaDisplay.App.SingleInstance.v1", out var created);
        if (!created)
        {
            MessageBox.Show("SAMA Display 已在运行，请查看系统托盘。", "SAMA Display",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureDirectory(AppPaths.UserDataDir);
        Logger.Configure(System.IO.Path.Combine(AppPaths.UserDataDir, "ui.log"));
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error(args.ExceptionObject as Exception ?? new Exception("unknown"), "AppDomain");

        base.OnStartup(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error(e.Exception, "Dispatcher");
        MessageBox.Show("发生未处理的错误：" + e.Exception.Message, "SAMA Display",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        base.OnExit(e);
    }
}
