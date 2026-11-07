using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using SamaDisplay.Core.Rendering;

namespace SamaDisplay.Core.Protocol;

/// <summary>
/// Minimal driver for the SAMA 3.4" USB-CDC display (VID_1D6B / PID_0134).
/// Wire format: fixed 250-byte packets, header EF 69, command byte, 32-bit
/// big-endian parameter, flag byte, payload from offset 10. Bulk image data is
/// sent as raw 250-byte chunks after a command header.
/// </summary>
public sealed class SamaDevice : IDisposable
{
    public const int PacketSize = 250;
    public const int PacketHeader = 10;
    public const int PayloadSize = PacketSize - PacketHeader;

    private readonly object _sync = new();
    private SerialPort? _port;
    private string? _portName;
    private volatile bool _open;
    private bool _rawInit;

    public bool IsOpen => _open;

    public string? PortName => _portName;
    public string? Version { get; private set; }

    public static string? FindPort()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_1D6B&PID_0134%'");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                var id = obj["PNPDeviceID"]?.ToString() ?? "";
                if (!id.Contains("MI_00", StringComparison.OrdinalIgnoreCase)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(name, @"\((COM\d+)\)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }

        try
        {
            foreach (var p in SerialPort.GetPortNames())
            {
                if (p.Equals("COM9", StringComparison.OrdinalIgnoreCase)) continue;
            }
        }
        catch { }
        return null;
    }

    public bool Open(string? portName = null, int baud = 115200)
    {
        lock (_sync)
        {
            CloseCore();
            var name = string.IsNullOrWhiteSpace(portName) ? FindPort() : portName;
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("未找到 SAMA 屏幕串口 (VID_1D6B&PID_0134)");

            var p = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
            {
                DtrEnable = true,
                RtsEnable = true,
                Handshake = Handshake.None,
                ReadTimeout = 200,
                WriteTimeout = 6000,
                ReadBufferSize = 8192,
                WriteBufferSize = 8192
            };
            p.Open();
            _port = p;
            _portName = name;
            _open = true;
            Version = null;
            Thread.Sleep(120);
            try { p.DiscardInBuffer(); } catch { }

            // The panel answers a version query with e.g. "chs_34inch.dev1_rom1.88".
            try
            {
                var resp = SendCommandCore(0x01, 1, new byte[] { 0xC5 }, 0, 1200);
                if (!string.IsNullOrWhiteSpace(resp) && resp.StartsWith("chs_", StringComparison.OrdinalIgnoreCase))
                    Version = resp.TrimEnd('\0', '\r', '\n');
            }
            catch { }
            return p.IsOpen;
        }
    }

    public void Close()
    {
        lock (_sync) { CloseCore(); }
    }

    private void CloseCore()
    {
        _open = false;
        _rawInit = false;
        try { _port?.Close(); } catch { }
        try { _port?.Dispose(); } catch { }
        _port = null;
        _portName = null;
        Version = null;
    }

    public string? QueryStatus()
    {
        lock (_sync) { return SendCommandCore(0xCF, 1, new byte[] { 0 }, 0, 800); }
    }

    /// <summary>Keep the panel in PC mode so its built-in theme video does not resume.</summary>
    public void Heartbeat()
    {
        lock (_sync)
        {
            if (_port == null || !_port.IsOpen) return;
            try
            {
                WritePacketCore(0x86, 1, new byte[] { 0 }, 0);
                WritePacketCore(0xCF, 1, new byte[] { 0 }, 0);
            }
            catch { }
        }
    }

    public void SetBrightness(byte level)
    {
        lock (_sync) { SendCommandCore(0x7B, 1, new[] { level }, 0, 0); }
    }

    public void SetRotate(byte rotate)
    {
        lock (_sync) { SendCommandCore(0x81, 1, new[] { rotate }, 0, 0); }
    }

    public string? StopMedia()
    {
        lock (_sync) { return SendCommandCore(0x96, 1, new byte[] { 0 }, 0, 1200); }
    }

    public string? PlayVideo(string devicePath, bool loop = true)
    {
        var bytes = Encoding.ASCII.GetBytes(devicePath);
        lock (_sync)
        {
            var r = SendCommandCore(0x78, (uint)bytes.Length, bytes, 1, 1500);
            return r;
        }
    }

    public string? GetFileSize(string devicePath, out int size)
    {
        size = -1;
        var bytes = Encoding.ASCII.GetBytes(devicePath);
        lock (_sync)
        {
            var r = SendCommandCore(0x6E, (uint)bytes.Length, bytes, 0, 1200);
            if (int.TryParse(r?.Trim(), out var v)) size = v;
            return r;
        }
    }

    /// <summary>
    /// Push a complete 480x480 JPEG to the panel. Command 0xC8 announces the
    /// payload length, then the JPEG is streamed in 250-byte chunks.
    /// </summary>
    public bool ShowImage(byte[] jpeg, out string message, out double milliseconds)
    {
        var sw = Stopwatch.StartNew();
        message = "";
        milliseconds = 0;
        lock (_sync)
        {
            if (_port == null || !_port.IsOpen) { message = "未连接"; return false; }
            try
            {
                try { _port.DiscardInBuffer(); } catch { }
                WritePacketCore(0xC8, (uint)jpeg.Length, null, 0);
                Thread.Sleep(100);
                SendRawCore(jpeg);

                var swRead = Stopwatch.StartNew();
                var sb = new StringBuilder();
                while (swRead.ElapsedMilliseconds < 6000)
                {
                    var part = ReadResponseCore(700);
                    if (string.IsNullOrEmpty(part))
                    {
                        if (sb.Length > 0) break;
                        continue;
                    }
                    sb.Append(part);
                    if (part.Contains("full_png_sucess", StringComparison.OrdinalIgnoreCase) ||
                        part.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                        part.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                        part.Contains("_err", StringComparison.OrdinalIgnoreCase))
                        break;
                }

                message = sb.ToString().TrimEnd('\0', '\r', '\n');
                Logger.Info($"ShowImage {jpeg.Length} bytes -> {(string.IsNullOrEmpty(message) ? "<no response>" : message)}");
                sw.Stop();
                milliseconds = sw.Elapsed.TotalMilliseconds;
                return message.Contains("full_png_sucess", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("success", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                sw.Stop();
                milliseconds = sw.Elapsed.TotalMilliseconds;
                message = ex.Message;
                CloseCore();
                return false;
            }
        }
    }

    /// <summary>
    /// Push a complete 480x480 BGRA frame as raw pixels (command 0xC8).
    /// The panel consumes 249 data bytes per 250-byte USB packet; the last
    /// byte is padding. Frames must be rotated 90 degrees clockwise by the
    /// renderer before calling this method.
    /// </summary>
    public bool ShowRawImage(byte[] bgra, out string message, out double milliseconds)
    {
        var sw = Stopwatch.StartNew();
        message = "";
        milliseconds = 0;
        lock (_sync)
        {
            if (_port == null || !_port.IsOpen) { message = "未连接"; return false; }
            try
            {
                try { _port.DiscardInBuffer(); } catch { }

                if (!_rawInit)
                {
                    WritePacketCore(0x96, 1, new byte[] { 0 }, 0);
                    Thread.Sleep(300);
                    WritePacketCore(0x86, 1, new byte[] { 0 }, 0);
                    Thread.Sleep(250);
                    _rawInit = true;
                }

                // One C8 keyframe per display session. Static mode sends this once;
                // the UI can request a fresh session to push updated values.
                for (var pass = 0; pass < 2; pass++)
                {
                    WritePacketCore(0xC8, (uint)bgra.Length, null, 0);
                    Thread.Sleep(60);
                    SendRaw249Core(bgra);
                    Thread.Sleep(150);
                }

                WritePacketCore(0x86, 1, new byte[] { 0 }, 0);
                Thread.Sleep(40);
                WritePacketCore(0xCF, 1, new byte[] { 0 }, 0);

                var sb = new StringBuilder();
                var part = ReadResponseCore(600);
                if (!string.IsNullOrEmpty(part)) sb.Append(part);

                message = sb.ToString().TrimEnd('\0', '\r', '\n');
                Logger.Info($"ShowRawImage {bgra.Length} bytes -> {(string.IsNullOrEmpty(message) ? "<no response>" : message)}");
                sw.Stop();
                milliseconds = sw.Elapsed.TotalMilliseconds;
                return !message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    && !message.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && !message.Contains("_err", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                sw.Stop();
                milliseconds = sw.Elapsed.TotalMilliseconds;
                message = ex.Message;
                CloseCore();
                return false;
            }
        }
    }

    /// <summary>
    /// Send one sparse delta frame (0xCC). The panel must already have a keyframe.
    /// Returns false with needResend=true when the panel asks for a full reload.
    /// </summary>
    public bool ShowDeltaFrame(byte[] reference, byte[] current, uint seq, out bool needResend,
        out string message, out double milliseconds)
    {
        var sw = Stopwatch.StartNew();
        needResend = false;
        message = "";
        milliseconds = 0;
        lock (_sync)
        {
            if (_port == null || !_port.IsOpen) { message = "未连接"; return false; }
            try
            {
                // Official order: query resend state first, then send the delta.
                WritePacketCore(0xCF, 1, new byte[] { 0 }, 0);
                var query = ReadResponseCore(1000);
                if (query.Contains("needReSend:1", StringComparison.OrdinalIgnoreCase))
                {
                    needResend = true;
                    message = query;
                    sw.Stop();
                    milliseconds = sw.Elapsed.TotalMilliseconds;
                    return false;
                }

                var delta = DeltaEncoder.Encode(reference, current, 4, out var deltaLen);
                if (delta == null || deltaLen <= 0)
                {
                    needResend = true;
                    message = "delta_empty";
                    sw.Stop();
                    milliseconds = sw.Elapsed.TotalMilliseconds;
                    return false;
                }

                var payload = new byte[deltaLen + 2];
                Buffer.BlockCopy(delta, 0, payload, 0, deltaLen);
                payload[deltaLen] = 0xEF;
                payload[deltaLen + 1] = 0x69;

                var header = new byte[8];
                header[0] = (byte)(seq >> 24);
                header[1] = (byte)(seq >> 16);
                header[2] = (byte)(seq >> 8);
                header[3] = (byte)seq;

                WritePacketCore(0xCC, (uint)payload.Length, header, 0);
                SendRaw249Core(payload);
                Thread.Sleep(120);

                var response = ReadResponseCore(1500);
                message = response;
                if (response.Contains("needReSend:1", StringComparison.OrdinalIgnoreCase))
                {
                    needResend = true;
                    Logger.Info($"ShowDelta seq={seq} delta={deltaLen} -> needResend");
                    sw.Stop();
                    milliseconds = sw.Elapsed.TotalMilliseconds;
                    return false;
                }

                Logger.Info($"ShowDelta seq={seq} delta={deltaLen} -> {(string.IsNullOrEmpty(response) ? "<no response>" : response)}");
                sw.Stop();
                milliseconds = sw.Elapsed.TotalMilliseconds;
                return true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                milliseconds = sw.Elapsed.TotalMilliseconds;
                message = ex.Message;
                CloseCore();
                return false;
            }
        }
    }

    /// <summary>Upload a file to the panel (experimental, sequence follows the official tool).</summary>
    public bool UploadFile(string devicePath, byte[] data, out string message)
    {
        message = "";
        var pathBytes = Encoding.ASCII.GetBytes(devicePath);
        lock (_sync)
        {
            try
            {
                var create = SendCommandCore(0x8C, (uint)pathBytes.Length, pathBytes, 0, 800);
                var off = 0;
                while (off < data.Length)
                {
                    var n = Math.Min(PayloadSize, data.Length - off);
                    var chunk = new byte[n];
                    Buffer.BlockCopy(data, off, chunk, 0, n);
                    var ack = SendCommandCore(0x6F, (uint)n, chunk, 0, 700);
                    if (ack.Contains("create_failed", StringComparison.OrdinalIgnoreCase))
                    {
                        message = ack.Trim();
                        return false;
                    }
                    off += n;
                }
                message = "upload_done";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                CloseCore();
                return false;
            }
        }
    }

    public string? SendCommand(byte cmd, uint param, byte[]? payload = null, byte flag = 0, int responseMs = 800)
    {
        lock (_sync) { return SendCommandCore(cmd, param, payload, flag, responseMs); }
    }

    private string? SendCommandCore(byte cmd, uint param, byte[]? payload, byte flag, int responseMs)
    {
        if (_port == null || !_port.IsOpen) throw new InvalidOperationException("串口未打开");
        try { _port.DiscardInBuffer(); } catch { }
        WritePacketCore(cmd, param, payload, flag);
        if (responseMs <= 0) return null;
        return ReadResponseCore(responseMs);
    }

    private void WritePacketCore(byte cmd, uint param, byte[]? payload, byte flag)
    {
        var pkt = new byte[PacketSize];
        pkt[0] = cmd;
        pkt[1] = 0xEF;
        pkt[2] = 0x69;
        pkt[3] = (byte)((param >> 24) & 0xFF);
        pkt[4] = (byte)((param >> 16) & 0xFF);
        pkt[5] = (byte)((param >> 8) & 0xFF);
        pkt[6] = (byte)(param & 0xFF);
        pkt[7] = flag;
        if (payload != null && payload.Length > 0)
        {
            var n = Math.Min(payload.Length, PayloadSize);
            Buffer.BlockCopy(payload, 0, pkt, PacketHeader, n);
        }
        _port!.Write(pkt, 0, pkt.Length);
    }

    private void SendRawCore(byte[] data)
    {
        var off = 0;
        while (off < data.Length)
        {
            var n = Math.Min(PacketSize, data.Length - off);
            var chunk = new byte[PacketSize];
            Buffer.BlockCopy(data, off, chunk, 0, n);
            _port!.Write(chunk, 0, chunk.Length);
            off += n;
            // 115200 8N1 = ~11.5 KB/s, i.e. ~22 ms per 250-byte packet.
            // Pace the CDC endpoint so the driver queue never overruns.
            Thread.Sleep(18);
        }
    }

    private void SendRaw249Core(byte[] data)
    {
        var off = 0;
        while (off < data.Length)
        {
            var n = Math.Min(249, data.Length - off);
            var chunk = new byte[PacketSize];
            Buffer.BlockCopy(data, off, chunk, 0, n);
            _port!.Write(chunk, 0, chunk.Length);
            off += n;
        }
    }

    private string ReadResponseCore(int timeoutMs)
    {
        var buf = new byte[4096];
        var ms = new MemoryStream();
        var sw = Stopwatch.StartNew();
        var idle = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var avail = 0;
            try { avail = _port!.BytesToRead; } catch { break; }
            if (avail > 0)
            {
                var n = _port.Read(buf, 0, Math.Min(avail, buf.Length));
                if (n > 0) { ms.Write(buf, 0, n); idle = 0; }
            }
            else
            {
                Thread.Sleep(10);
                idle += 10;
                if (ms.Length > 0 && idle >= 60) break;
                if (ms.Length == 0 && idle >= 80) break;
            }
        }
        if (ms.Length == 0) return string.Empty;
        var bytes = ms.ToArray();
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, end);
    }

    public void Dispose() => Close();
}
