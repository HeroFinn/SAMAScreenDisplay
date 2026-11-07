using System;
using System.IO;
using System.Text;

namespace SamaDisplay.Core;

public static class Logger
{
    private static readonly object Gate = new();
    private static string _path = AppPaths.ServiceLog;

    public static void Configure(string path)
    {
        _path = path;
        AppPaths.EnsureDirectory(Path.GetDirectoryName(path)!);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(Exception ex, string? context = null) =>
        Write("ERROR", (context == null ? "" : context + ": ") + ex);

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                AppPaths.EnsureDirectory(Path.GetDirectoryName(_path)!);
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                {
                    var old = _path + ".old";
                    try { File.Delete(old); } catch { }
                    try { File.Move(_path, old); } catch { }
                }
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
