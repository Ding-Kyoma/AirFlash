using System.Text.Json;
namespace AirFlash.Core;

public static class EngineHandshake
{
    public static async Task<JsonElement> HelloAsync(IEngineFactory factory, CancellationToken cancellation = default, TimeSpan? limit = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(limit ?? TimeSpan.FromSeconds(10));
        await using var connection = factory.Open();
        try
        {
            await connection.SendAsync("check", "hello", null, timeout.Token).ConfigureAwait(false);
            var hello = await connection.ReadAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new IOException(connection is ProcessEngineConnection process ? process.FailureDescription : L.Get("The engine did not return hello."));
            if (hello.Text("event") != "hello" || hello.Number("version") != 1)
                throw new IOException(L.Get("Incompatible engine protocol version."));
            return hello;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException(L.Get("Audio engine initialization timed out after 10 seconds."));
        }
    }
}
