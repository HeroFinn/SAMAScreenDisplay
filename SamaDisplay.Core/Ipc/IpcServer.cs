using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SamaDisplay.Core.Ipc;

public sealed class IpcServer : IDisposable
{
    private readonly Func<IpcRequest, IpcResponse> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public IpcServer(Func<IpcRequest, IpcResponse> handler) => _handler = handler;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SamaDisplay.Ipc" };
        _thread.Start();
    }

    private async void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = PipeFactory.CreateServer();
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => Handle(pipe));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Warn("IPC accept failed: " + ex.Message);
                try { pipe?.Dispose(); } catch { }
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private async Task Handle(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 8192, leaveOpen: true))
            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true })
            {
                while (pipe.IsConnected && !_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    IpcResponse response;
                    try
                    {
                        var req = JsonSerializer.Deserialize<IpcRequest>(line);
                        if (req == null)
                        {
                            response = IpcResponse.Fail("invalid request");
                        }
                        else
                        {
                            var task = Task.Run(() => _handler(req));
                            var done = await Task.WhenAny(task, Task.Delay(12000)).ConfigureAwait(false);
                            response = done == task && task.IsCompletedSuccessfully
                                ? task.Result
                                : IpcResponse.Fail("engine busy");
                        }
                    }
                    catch (Exception ex)
                    {
                        response = IpcResponse.Fail(ex.Message);
                    }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
