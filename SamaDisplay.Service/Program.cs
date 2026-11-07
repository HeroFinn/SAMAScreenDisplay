using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using SamaDisplay.Core;
using SamaDisplay.Core.Engine;
using SamaDisplay.Core.Ipc;

namespace SamaDisplay.Service;

internal static class Program
{
    private static void Main(string[] args)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        if (args.Any(a => a.Equals("--console", StringComparison.OrdinalIgnoreCase)))
        {
            RunConsole();
            return;
        }

        ServiceBase.Run(new SamaWindowsService());
    }

    private static void RunConsole()
    {
        Console.Title = "SAMA Display Service (console)";
        Logger.Configure(Path.Combine(AppContext.BaseDirectory, "service-console.log"));
        using var engine = new DisplayEngine(AppPaths.MachineSettings, true);
        using var ipc = new IpcServer(engine.Handle);
        engine.Start();
        ipc.Start();
        Console.WriteLine("SAMA Display service running in console mode. Press Enter to stop.");
        Console.ReadLine();
    }
}

public sealed class SamaWindowsService : ServiceBase
{
    private DisplayEngine? _engine;
    private IpcServer? _ipc;

    public SamaWindowsService()
    {
        ServiceName = AppPaths.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            Logger.Configure(AppPaths.ServiceLog);
            AppPaths.EnsureDirectory(AppPaths.MachineDataDir);
            Logger.Info("Service starting");
            _engine = new DisplayEngine(AppPaths.MachineSettings, true);
            _ipc = new IpcServer(_engine.Handle);
            _engine.Start();
            _ipc.Start();
            Logger.Info("Service started");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "OnStart");
            throw;
        }
    }

    protected override void OnStop()
    {
        Logger.Info("Service stopping");
        try { _ipc?.Dispose(); } catch { }
        try { _engine?.Dispose(); } catch { }
        _ipc = null;
        _engine = null;
        Logger.Info("Service stopped");
    }

    protected override void OnShutdown()
    {
        OnStop();
        base.OnShutdown();
    }
}
