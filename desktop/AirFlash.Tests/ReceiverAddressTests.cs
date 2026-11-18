using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class ReceiverAddressTests
{
    private static readonly JsonElement Hello = JsonDocument.Parse("""
        {"version":1,"session_id":"query","event":"hello","capabilities":["receiver_capabilities"]}
        """).RootElement.Clone();

    [Fact]
    public async Task Capability_queries_use_the_addresses_the_session_already_resolved()
    {
        // `.invalid` never resolves (RFC 6761): the query must not look it up again.
        var receiver = new Receiver("pre-resolved", "Pre-resolved", "speaker.invalid", 7000);
        var connection = Answering(receiver.Id, "192.0.2.77");
        var set = await ReceiverCapabilityCatalog.QueryConnectionAsync(connection, "query", Hello, receiver, CancellationToken.None,
            new Dictionary<string, string> { [receiver.Id] = "192.0.2.77" }).WaitAsync(SessionTestEngine.Limit);
        Assert.True(set.Known);
        Assert.Equal("192.0.2.77", connection.Last("get_receiver_capabilities").GetProperty("peers")[0].GetProperty("host").GetString());
    }

    [Fact]
    public async Task Capability_queries_pass_ipv4_literals_through()
    {
        var receiver = new Receiver("literal", "Literal", "192.0.2.78", 7000);
        var connection = Answering(receiver.Id, receiver.Address);
        await ReceiverCapabilityCatalog.QueryConnectionAsync(connection, "query", Hello, receiver, CancellationToken.None).WaitAsync(SessionTestEngine.Limit);
        Assert.Equal("192.0.2.78", connection.Last("get_receiver_capabilities").GetProperty("peers")[0].GetProperty("host").GetString());
    }

    private static SessionTestEngine.Connection Answering(string id, string host) => new()
    {
        OnCommand = (connection, command) =>
        {
            if (command != "get_receiver_capabilities") return;
            connection.Current("receiver_capabilities", new { members = new[] {
                new { receiver_id = id, host, port = 7000, known = true, profiles = new[] {
                    new { codec = "auto", transport = "auto", timing = "auto", sample_rate = 44100,
                        selected_codec = "alac", selected_transport = "realtime", selected_timing = "ptp" } } } } });
        }
    };
}
