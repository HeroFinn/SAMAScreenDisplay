using System;

namespace SamaDisplay.Core.Ipc;

public sealed class IpcRequest
{
    public string Cmd { get; set; } = "";
    public int? Value { get; set; }
    public string? Text { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public StatusDto? Status { get; set; }

    public static IpcResponse Fail(string error) => new() { Ok = false, Error = error };
    public static IpcResponse Success(string? message = null) => new() { Ok = true, Message = message };
}

public sealed class StatusDto
{
    public bool Connected { get; set; }
    public string? Port { get; set; }
    public string? Version { get; set; }
    public bool Displaying { get; set; }
    public long FramesSent { get; set; }
    public double LastFrameMs { get; set; }
    public string? LastError { get; set; }
    public int Brightness { get; set; }
    public int IntervalSeconds { get; set; }
    public string Theme { get; set; } = "Dark";
    public string? DeviceVideo { get; set; }
    public bool ServiceMode { get; set; }
    public SensorSnapshot Sensors { get; set; } = new();
    public DateTime ServiceStarted { get; set; }
}
