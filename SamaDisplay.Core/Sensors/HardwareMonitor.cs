using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace SamaDisplay.Core.Sensors;

public sealed class HardwareMonitor : IDisposable
{
    private readonly object _gate = new();
    private SensorSnapshot _current = new();
    private Computer? _computer;
    private Timer? _timer;
    private bool _lhmReady;
    private bool _started;
    private string _cpuName = "CPU";
    private string _gpuName = "GPU";

    private ulong _prevIdle, _prevKernel, _prevUser;
    private double _cpuLoadFallback;
    private bool _haveCpuTimes;
    private bool _loggedClocks;

    public SensorSnapshot GetSnapshot()
    {
        return System.Threading.Volatile.Read(ref _current).Clone();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        AppPaths.EnsureDirectory(AppPaths.MachineDataDir);

        _cpuName = ReadCpuName();
        _gpuName = ReadGpuName();

        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = false,
                IsControllerEnabled = true,
                IsNetworkEnabled = false
            };
            _computer.Open();
            _lhmReady = true;
            Logger.Info("LibreHardwareMonitor initialized");
        }
        catch (Exception ex)
        {
            _lhmReady = false;
            Logger.Warn("LibreHardwareMonitor unavailable, using fallback sensors: " + ex.Message);
        }

        _timer = new Timer(_ => Update(), null, 200, 1000);
    }

    private void Update()
    {
        if (Interlocked.Exchange(ref _updating, 1) == 1) return;
        try
        {
            var snap = new SensorSnapshot
            {
                Timestamp = DateTime.Now,
                HostName = Environment.MachineName,
                Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
                CpuName = _cpuName,
                GpuName = _gpuName,
                HardwareAvailable = _lhmReady
            };

            if (_lhmReady && _computer != null)
            {
                try { ReadLhm(snap); }
                catch (Exception ex)
                {
                    Logger.Warn("Sensor read failed: " + ex.Message);
                }
            }

            if (snap.CpuLoad == null) snap.CpuLoad = ReadCpuLoadFallback();
            ReadMemoryFallback(snap);

            System.Threading.Volatile.Write(ref _current, snap);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "HardwareMonitor.Update");
        }
        finally
        {
            Interlocked.Exchange(ref _updating, 0);
        }
    }

    private int _updating;

    private void ReadLhm(SensorSnapshot snap)
    {
        IHardware? cpu = null, gpu = null, memory = null;
        var motherboardFans = new List<ISensor>();

        foreach (var hw in _computer!.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu ??= hw;
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpu ??= hw;
                    break;
                case HardwareType.Memory:
                    memory ??= hw;
                    break;
                case HardwareType.Motherboard:
                    CollectFans(hw, motherboardFans);
                    break;
            }
        }

        if (cpu != null)
        {
            snap.CpuName = string.IsNullOrWhiteSpace(cpu.Name) ? _cpuName : cpu.Name;
            if (!_loggedClocks)
            {
                _loggedClocks = true;
                try
                {
                    Logger.Info("CPU clock sensors: " + string.Join("; ",
                        cpu.Sensors.Where(x => x.SensorType == SensorType.Clock)
                            .Select(x => $"{x.Name}={x.Value:0}")));
                }
                catch { }
            }
            snap.CpuTemp = ReadTemp(cpu, "Tctl", "Tdie", "Package", "CPU Package", "Core Average");
            snap.CpuLoad = ReadValue(cpu, SensorType.Load, "CPU Total", "Total");
            snap.CpuClockMhz = ReadClock(cpu);
            snap.CpuPowerW = ReadValue(cpu, SensorType.Power, "Package", "CPU Package");
            snap.CpuFanRpm = ReadValue(cpu, SensorType.Fan, "CPU");
            if (snap.CpuFanRpm == null && motherboardFans.Count > 0)
                snap.CpuFanRpm = motherboardFans[0].Value;
        }

        if (gpu != null)
        {
            snap.GpuName = string.IsNullOrWhiteSpace(gpu.Name) ? _gpuName : gpu.Name;
            snap.GpuTemp = ReadTemp(gpu, "GPU Core", "GPU", "Hot Spot");
            snap.GpuLoad = ReadValue(gpu, SensorType.Load, "GPU Core", "GPU");
            snap.GpuClockMhz = ReadValue(gpu, SensorType.Clock, "GPU Core", "GPU");
            snap.GpuFanRpm = ReadValue(gpu, SensorType.Fan, "GPU Fan", "Fan");
            snap.GpuMemUsedMb = ReadValue(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
            snap.GpuMemTotalMb = ReadValue(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
        }

        if (memory != null)
        {
            snap.RamPercent = ReadValue(memory, SensorType.Load, "Memory");
            snap.RamUsedGb = ReadValue(memory, SensorType.Data, "Used Memory");
            snap.RamTotalGb = ReadValue(memory, SensorType.Data, "Available Memory");
            if (snap.RamUsedGb != null && snap.RamTotalGb != null)
                snap.RamTotalGb += snap.RamUsedGb; // "Available" + "Used" = total
        }

        if (snap.CpuFanRpm == null && motherboardFans.Count > 0)
            snap.CpuFanRpm = motherboardFans[0].Value;
    }

    private static void CollectFans(IHardware hw, List<ISensor> fans)
    {
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Fan && s.Value is > 0) fans.Add(s);
        foreach (var sub in hw.SubHardware)
            CollectFans(sub, fans);
    }

    private static double? ReadTemp(IHardware hw, params string[] names)
    {
        foreach (var name in names)
        {
            var s = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature &&
                x.Value is > 0 && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        var any = hw.Sensors.Where(x => x.SensorType == SensorType.Temperature && x.Value is > 0)
            .OrderByDescending(x => x.Value).FirstOrDefault();
        return any?.Value;
    }

    private static double? ReadValue(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var s = hw.Sensors.FirstOrDefault(x => x.SensorType == type &&
                x.Value != null && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        return null;
    }

    private static double? ReadClock(IHardware hw)
    {
        var values = hw.Sensors.Where(x => x.SensorType == SensorType.Clock &&
                x.Value is > 100 and < 6000)
            .Select(x => x.Value!.Value).ToList();
        return values.Count > 0 ? values.Max() : null;
    }

    private double? ReadCpuLoadFallback()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return _cpuLoadFallback;
            ulong i = ToUlong(idle), k = ToUlong(kernel), u = ToUlong(user);
            if (_haveCpuTimes)
            {
                var idleDelta = i - _prevIdle;
                var totalDelta = (k - _prevKernel) + (u - _prevUser);
                if (totalDelta > 0)
                    _cpuLoadFallback = Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100);
            }
            _prevIdle = i; _prevKernel = k; _prevUser = u; _haveCpuTimes = true;
            return _cpuLoadFallback;
        }
        catch { return null; }
    }

    private static void ReadMemoryFallback(SensorSnapshot snap)
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                var total = status.ullTotalPhys / 1024.0 / 1024 / 1024;
                var avail = status.ullAvailPhys / 1024.0 / 1024 / 1024;
                snap.RamTotalGb ??= total;
                snap.RamUsedGb ??= total - avail;
                snap.RamPercent ??= total > 0 ? (total - avail) / total * 100 : null;
            }
        }
        catch { }
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString")?.ToString();
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch { }
        return "CPU";
    }

    private static string ReadGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch { }
        return "GPU";
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        try { _computer?.Close(); } catch { }
        _computer = null;
        _lhmReady = false;
    }

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
