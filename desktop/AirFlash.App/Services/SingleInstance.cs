using System.IO.Pipes;
using System.Security.Principal;
namespace AirFlash.App.Services;

public sealed class SingleInstance : IAsyncDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _server;
    public bool IsOwner { get; }
    public SingleInstance(string? isolatedSuffix = null)
    {
        var suffix = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = "AirFlash.Wpf." + suffix + (isolatedSuffix is null ? "" : "." + isolatedSuffix);
        _mutex = new(true, @"Local\" + _pipeName, out var created);
        IsOwner = created;
    }
    public async Task NotifyExistingAsync()
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000); await pipe.WriteAsync(new byte[] { 1 }); await pipe.FlushAsync();
    }
    public void Listen(Action activate) => _server = Task.Run(async () =>
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var input = new byte[1]; if (await pipe.ReadAsync(input, _stop.Token) == 1) activate();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (IOException error) { AppPaths.Log(error.ToString()); }
    });
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); if (_server is not null) await _server;
        if (IsOwner) _mutex.ReleaseMutex(); _mutex.Dispose(); _stop.Dispose();
    }
}
