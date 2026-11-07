using System;
using System.IO;

namespace SamaDisplay.Core;

public static class AppPaths
{
    public const string ServiceName = "SamaDisplaySvc";
    public const string DisplayName = "SAMA Display Service";
    public const string PipeName = "SamaDisplay.IPC";

    public static string MachineDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SamaDisplay");

    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SamaDisplay");

    public static string MachineSettings => Path.Combine(MachineDataDir, "settings.json");
    public static string UserSettings => Path.Combine(UserDataDir, "settings.json");
    public static string ServiceLog => Path.Combine(MachineDataDir, "service.log");

    public static string EnsureDirectory(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        return path;
    }
}
