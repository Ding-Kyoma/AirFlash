using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class ReceiverCapabilityTests
{
    private static ReceiverProfile Profile(int rate, string codec = "auto", string selectedCodec = "alac", string transport = "auto", string timing = "auto") => new(codec, transport, timing, rate, selectedCodec, "realtime", "ptp");
    [Fact]
    public void AutomaticRatePrefers44100AndIntersectsNativePairMembers()
    {
        var a = new ReceiverCapabilities("a", "127.0.0.1", 7000, true, [Profile(44100), Profile(48000)]);
        var b = new ReceiverCapabilities("b", "127.0.0.2", 7000, true, [Profile(48000)]);
        Assert.Equal(44100, new ReceiverCapabilitySet([a]).Select(new ReceiverOptions()).Single().Profile.SampleRate);
        var pair = new ReceiverCapabilitySet([a, b]);
        Assert.All(pair.Select(new ReceiverOptions()), s => Assert.Equal(48000, s.Profile.SampleRate));
        Assert.Empty(pair.Select(new ReceiverOptions(), "44100"));
    }
    [Fact]
    public void SoftwarePairUsesCommonRateAndEachMembersOwnProfile()
    {
        var set = new ReceiverCapabilitySet([
            new("a", "127.0.0.1", 7000, true, [Profile(44100, "pcm", "pcm"), Profile(48000, "pcm", "pcm")]),
            new("b", "127.0.0.2", 7000, true, [Profile(48000, "alac")])
        ]);
        var settings = new AppSettings(); settings.Options("a").Codec = "pcm"; settings.Options("b").Codec = "alac";
        var selected = set.Select(settings.ReadOptions, "48000");
        Assert.Equal(2, selected.Count); Assert.Equal("pcm", selected[0].Profile.SelectedCodec); Assert.Equal("alac", selected[1].Profile.SelectedCodec);
        Assert.Empty(set.Select(settings.ReadOptions, "44100"));
        Assert.Empty(set.Select(new ReceiverOptions(), "48000"));
    }
    [Fact]
    public void UnknownAndInvalidProfilesRemainDistinct()
    {
        Assert.False(ReceiverCapabilitySet.Unknown.Known); Assert.Empty(ReceiverCapabilitySet.Unknown.Select(new ReceiverOptions()));
        var known = new ReceiverCapabilitySet([new("a", "127.0.0.1", 7000, true, [Profile(44100)])]);
        Assert.True(known.Known); Assert.Empty(known.Select(new ReceiverOptions { Codec = "pcm" }));
    }
    [Fact]
    public async Task CatalogUsesOnlyHelloAndInfoQueryAndValidatesReceiverIdentity()
    {
        var connection = new QueryConnection();
        var catalog = new ReceiverCapabilityCatalog(new QueryFactory(connection));
        var result = await catalog.QueryAsync(new("a", "A", "127.0.0.1"));
        Assert.True(result.Known); Assert.Equal(48000, result.Select(new ReceiverOptions()).Single().Profile.SampleRate);
        Assert.Equal(new[] { "hello", "get_receiver_capabilities" }, connection.Commands); Assert.True(connection.Disposed);
        connection = new QueryConnection { WrongIdentity = true };
        await Assert.ThrowsAsync<IOException>(() => new ReceiverCapabilityCatalog(new QueryFactory(connection)).QueryAsync(new("a", "A", "127.0.0.1")));
    }
    private sealed class QueryFactory(QueryConnection connection) : IEngineFactory { public IEngineConnection Open() => connection; }
    private sealed class QueryConnection : IEngineConnection
    {
        public List<string> Commands { get; } = [];
        public bool WrongIdentity, Disposed;
        private string _session = "";
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation) { _session = session; Commands.Add(command); return Task.CompletedTask; }
        public ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation) => ValueTask.FromResult<JsonElement?>(Commands.Last() == "hello"
            ? JsonSerializer.SerializeToElement(new { version = 1, session_id = _session, @event = "hello", capabilities = new[] { "receiver_capabilities" } })
            : JsonSerializer.SerializeToElement(new { version = 1, session_id = _session, @event = "receiver_capabilities", members = new[] { new { receiver_id = WrongIdentity ? "other" : "a", host = "127.0.0.1", port = 7000, known = true, profiles = new[] { new { codec = "auto", transport = "auto", timing = "auto", sample_rate = 48000, selected_codec = "alac", selected_transport = "realtime", selected_timing = "ptp" } } } } }));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
