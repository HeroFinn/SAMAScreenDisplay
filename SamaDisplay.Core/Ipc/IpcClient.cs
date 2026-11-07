using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SamaDisplay.Core.Ipc;

/// <summary>
/// Small JSON-over-named-pipe client. Keeps one connection alive because opening
/// a new pipe for every 1-second status poll is surprisingly expensive on Windows.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public async Task<IpcResponse?> SendAsync(IpcRequest request, int timeoutMs = 15000)
    {
        if (!await _gate.WaitAsync(timeoutMs).ConfigureAwait(false)) return null;
        try
        {
            if (!await EnsureConnectedAsync(timeoutMs).ConfigureAwait(false)) return null;
            await _writer!.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            var line = await _reader!.ReadLineAsync()
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
            return line == null ? null : JsonSerializer.Deserialize<IpcResponse>(line);
        }
        catch (Exception ex)
        {
            Logger.Warn("IPC request failed: " + ex.GetType().Name + ": " + ex.Message);
            DisposePipe();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureConnectedAsync(int timeoutMs)
    {
        if (_pipe is { IsConnected: true } && _reader != null && _writer != null) return true;
        DisposePipe();
        var pipe = new NamedPipeClientStream(".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            return false;
        }
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
        return true;
    }

    private void DisposePipe()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public Task<IpcResponse?> StatusAsync(int timeoutMs = 15000) =>
        SendAsync(new IpcRequest { Cmd = "status" }, timeoutMs);

    public void Dispose()
    {
        DisposePipe();
        _gate.Dispose();
    }
}
