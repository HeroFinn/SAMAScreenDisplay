using System;

namespace SamaDisplay.Core;

public sealed class SensorSnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string HostName { get; set; } = Environment.MachineName;
    public TimeSpan Uptime { get; set; }
    public bool HardwareAvailable { get; set; }

    public string CpuName { get; set; } = "CPU";
    public double? CpuLoad { get; set; }
    public double? CpuTemp { get; set; }
    public double? CpuClockMhz { get; set; }
    public double? CpuPowerW { get; set; }
    public double? CpuFanRpm { get; set; }

    public string GpuName { get; set; } = "GPU";
    public double? GpuLoad { get; set; }
    public double? GpuTemp { get; set; }
    public double? GpuClockMhz { get; set; }
    public double? GpuFanRpm { get; set; }
    public double? GpuMemUsedMb { get; set; }
    public double? GpuMemTotalMb { get; set; }

    public double? RamUsedGb { get; set; }
    public double? RamTotalGb { get; set; }
    public double? RamPercent { get; set; }

    public double? Fps { get; set; }

    public SensorSnapshot Clone() => (SensorSnapshot)MemberwiseClone();
}
