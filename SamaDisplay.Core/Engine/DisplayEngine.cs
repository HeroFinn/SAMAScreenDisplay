using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using SamaDisplay.Core.Ipc;
using SamaDisplay.Core.Protocol;
using SamaDisplay.Core.Rendering;
using SamaDisplay.Core.Sensors;

namespace SamaDisplay.Core.Engine;

public sealed class DisplayEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly bool _serviceMode;
    private readonly SamaDevice _device = new();
    private readonly HardwareMonitor _sensors = new();
    private readonly DashboardRenderer _renderer = new();
    private AppSettings _settings;
    private Thread? _thread;
    private Thread? _heartbeat;
    private volatile bool _running;
    private volatile bool _displaying;
    private bool _needKeyframe = true;
    private byte[]? _lastBgra;
    private uint _seq;
    private string? _lastError;
    private long _frames;
    private double _lastFrameMs;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly ManualResetEventSlim _wake = new(false);

    public DisplayEngine(string settingsPath, bool serviceMode)
    {
        _settingsPath = settingsPath;
        _serviceMode = serviceMode;
        _settings = AppSettings.Load(settingsPath);
        _displaying = _settings.AutoStartDisplay;
    }

    public AppSettings Settings
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SamaDisplay.Engine" };
        _thread.Start();
    }

    private void HeartbeatLoop()
    {
        while (_running)
        {
            try
            {
                if (_device.IsOpen) _device.Heartbeat();
            }
            catch { }
            Thread.Sleep(900);
        }
    }

    private void Loop()
    {
        Logger.Info($"Engine started (service={_serviceMode}, settings={_settingsPath})");
        try { _sensors.Start(); } catch (Exception ex) { Logger.Error(ex, "sensor start"); }
        var lastConnectAttempt = DateTime.MinValue;

        while (_running)
        {
            var frameSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!_device.IsOpen)
                {
                    if ((DateTime.Now - lastConnectAttempt).TotalSeconds >= 5)
                    {
                        lastConnectAttempt = DateTime.Now;
                        TryConnect();
                    }
                }

                if (_device.IsOpen && _displaying)
                {
                    var snap = _sensors.GetSnapshot();
                    AppSettings cfg;
                    lock (_gate) cfg = _settings.Clone();
                    var bgra = _renderer.RenderBgra(snap, cfg);
                    Logger.Info($"frame {snap.Timestamp:HH:mm:ss.fff} cpu={snap.CpuTemp:F0} load={snap.CpuLoad:F0}");

                    bool ok;
                    string message;
                    double ms;
                    if (_needKeyframe || _lastBgra == null)
                    {
                        ok = _device.ShowRawImage(bgra, out message, out ms);
                        if (ok)
                        {
                            _lastBgra = bgra;
                            _seq = 0;
                            _needKeyframe = false;
                        }
                    }
                    else
                    {
                        ok = _device.ShowDeltaFrame(_lastBgra, bgra, _seq, out var needResend, out message, out ms);
                        if (ok)
                        {
                            _lastBgra = bgra;
                            _seq++;
                        }
                        else if (needResend)
                        {
                            // Official recovery: reload the full keyframe, then seq resets to 0.
                            _needKeyframe = true;
                        }
                    }

                    if (ok)
                    {
                        _lastFrameMs = ms;
                        _frames++;
                        _lastError = null;
                    }
                    else if (!_needKeyframe)
                    {
                        _lastError = string.IsNullOrWhiteSpace(message) ? "\u53D1\u9001\u5931\u8D25" : message;
                        Logger.Warn("ShowDelta failed: " + _lastError);
                        _device.Close();
                        _needKeyframe = true;
                        lastConnectAttempt = DateTime.MinValue;
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Logger.Error(ex, "engine loop");
                try { _device.Close(); } catch { }
                lastConnectAttempt = DateTime.MinValue;
            }

            int wait;
            lock (_gate) wait = Math.Clamp(_settings.IntervalSeconds, 1, 60) * 1000;
            frameSw.Stop();
            _wake.Wait(Math.Max(1000, wait - (int)frameSw.ElapsedMilliseconds));
            _wake.Reset();
        }

        try { _device.Close(); } catch { }
        try { _sensors.Dispose(); } catch { }
        Logger.Info("Engine stopped");
    }

    private void TryConnect()
    {
        try
        {
            AppSettings cfg;
            lock (_gate) cfg = _settings.Clone();
            _device.Open(cfg.Port);
            if (!_device.IsOpen) throw new InvalidOperationException("open failed");
            try { _device.SetBrightness((byte)Math.Clamp(cfg.Brightness, 0, 255)); } catch { }
            try { _device.SetRotate((byte)Math.Clamp(cfg.Rotate, 0, 3)); } catch { }
            if (!string.IsNullOrWhiteSpace(cfg.DeviceVideo))
            {
                try { _device.StopMedia(); _device.PlayVideo(cfg.DeviceVideo); } catch { }
            }
            else
            {
                // Leave video mode so full-frame images are visible.
                try { _device.StopMedia(); } catch { }
            }
            Thread.Sleep(400);
            _lastError = null;
            _needKeyframe = true;
            _lastBgra = null;
            _seq = 0;
            Logger.Info($"Connected on {_device.PortName}, firmware {_device.Version}");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logger.Warn("Connect failed: " + ex.Message);
            try { _device.Close(); } catch { }
        }
    }

    public void StartDisplay()
    {
        lock (_gate) { _settings.AutoStartDisplay = true; _settings.Save(_settingsPath); }
        _needKeyframe = true;
        _displaying = true;
        _wake.Set();
    }

    public void StopDisplay()
    {
        lock (_gate) { _settings.AutoStartDisplay = false; _settings.Save(_settingsPath); }
        _displaying = false;
        _wake.Set();
    }

    public StatusDto GetStatus()
    {
        AppSettings cfg;
        lock (_gate) cfg = _settings.Clone();
        return new StatusDto
        {
            Connected = _device.IsOpen,
            Port = _device.PortName,
            Version = _device.Version,
            Displaying = _displaying,
            FramesSent = Interlocked.Read(ref _frames),
            LastFrameMs = _lastFrameMs,
            LastError = _lastError,
            Brightness = cfg.Brightness,
            IntervalSeconds = cfg.IntervalSeconds,
            Theme = cfg.Theme,
            DeviceVideo = cfg.DeviceVideo,
            ServiceMode = _serviceMode,
            Sensors = _sensors.GetSnapshot(),
            ServiceStarted = _startedAt
        };
    }

    public IpcResponse Handle(IpcRequest request)
    {
        try
        {
            switch ((request.Cmd ?? "").ToLowerInvariant())
            {
                case "status":
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                case "start":
                    StartDisplay();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                case "stop":
                    StopDisplay();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                case "brightness":
                {
                    var value = Math.Clamp(request.Value ?? 170, 0, 255);
                    lock (_gate) { _settings.Brightness = value; _settings.Save(_settingsPath); }
                    try { if (_device.IsOpen) _device.SetBrightness((byte)value); } catch { }
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "interval":
                {
                    var value = Math.Clamp(request.Value ?? 2, 1, 60);
                    lock (_gate) { _settings.IntervalSeconds = value; _settings.Save(_settingsPath); }
                    _wake.Set();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "port":
                {
                    var port = (request.Text ?? "").Trim();
                    lock (_gate)
                    {
                        _settings.Port = string.IsNullOrWhiteSpace(port) || port == "\u81EA\u52A8\u68C0\u6D4B" ? null : port;
                        _settings.Save(_settingsPath);
                    }
                    try { _device.Close(); } catch { }
                    _wake.Set();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "theme":
                {
                    lock (_gate) { _settings.Theme = request.Text ?? "Dark"; _settings.Save(_settingsPath); }
                    _wake.Set();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "rotate":
                {
                    var value = Math.Clamp(request.Value ?? 0, 0, 3);
                    lock (_gate) { _settings.Rotate = value; _settings.Save(_settingsPath); }
                    try { if (_device.IsOpen) _device.SetRotate((byte)value); } catch { }
                    _wake.Set();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "quality":
                {
                    var value = Math.Clamp(request.Value ?? 40, 25, 95);
                    lock (_gate) { _settings.JpegQuality = value; _settings.Save(_settingsPath); }
                    _wake.Set();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "fahrenheit":
                {
                    var value = (request.Value ?? 0) != 0;
                    lock (_gate) { _settings.Fahrenheit = value; _settings.Save(_settingsPath); }
                    _wake.Set();
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "video":
                {
                    var path = request.Text ?? "";
                    lock (_gate) { _settings.DeviceVideo = string.IsNullOrWhiteSpace(path) ? null : path; _settings.Save(_settingsPath); }
                    _displaying = false;
                    if (!string.IsNullOrWhiteSpace(path) && _device.IsOpen)
                    {
                        try { _device.StopMedia(); var r = _device.PlayVideo(path); Logger.Info("PlayVideo " + path + " => " + r); }
                        catch (Exception ex) { return IpcResponse.Fail(ex.Message); }
                    }
                    return new IpcResponse { Ok = true, Status = GetStatus() };
                }
                case "ping":
                    return IpcResponse.Success("pong");
                default:
                    return IpcResponse.Fail("unknown command: " + request.Cmd);
            }
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        try { _thread?.Join(3000); } catch { }
        try { _heartbeat?.Join(1500); } catch { }
        _wake.Dispose();
        _device.Dispose();
        _sensors.Dispose();
    }
}

