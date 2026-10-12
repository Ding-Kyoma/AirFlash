using System.Net;
using System.Net.Sockets;
using System.Text.Json;
namespace AirFlash.Core;

public sealed record ReceiverProfile(string Codec, string Transport, string Timing, int SampleRate,
    string SelectedCodec, string SelectedTransport, string SelectedTiming)
{
    public bool Matches(ReceiverOptions options, int rate) => Codec == options.Codec && Transport == options.Transport && Timing == options.Timing && SampleRate == rate;
}
public sealed record ReceiverCapabilities(string ReceiverId, string Host, int Port, bool Known, ReceiverProfile[] Profiles);
public sealed record ReceiverProfileSelection(string ReceiverId, ReceiverProfile Profile);
public sealed class ReceiverCapabilitySet(IReadOnlyList<ReceiverCapabilities> members)
{
    public static ReceiverCapabilitySet Unknown { get; } = new([]);
    public IReadOnlyList<ReceiverCapabilities> Members { get; } = members;
    public bool Known => Members.Count > 0 && Members.All(m => m.Known);
    public IReadOnlyList<ReceiverProfileSelection> Select(ReceiverOptions options, string? sampleRate = null)
        => Select(_ => options, sampleRate);
    public IReadOnlyList<ReceiverProfileSelection> Select(Func<string, ReceiverOptions> options, string? sampleRate = null)
    {
        if (Members.Count == 0) return [];
        var rates = sampleRate is null ? new[] { 44100, 48000 } : int.TryParse(sampleRate, out var rate) ? [rate] : Array.Empty<int>();
        foreach (var candidate in rates)
        {
            var selected = Members.Select(m => new { Member = m, Profile = m.Profiles.FirstOrDefault(p => p.Matches(options(m.ReceiverId), candidate)) }).ToArray();
            if (selected.All(m => m.Profile is not null)) return selected.Select(m => new ReceiverProfileSelection(m.Member.ReceiverId, m.Profile!)).ToArray();
        }
        return [];
    }
}
public interface IReceiverCapabilityCatalog
{
    Task<ReceiverCapabilitySet> QueryAsync(Receiver receiver, CancellationToken cancellation = default);
}
public sealed class ReceiverCapabilityCatalog(IEngineFactory factory) : IReceiverCapabilityCatalog
{
    private readonly SemaphoreSlim _queries = new(2, 2);
    public async Task<ReceiverCapabilitySet> QueryAsync(Receiver receiver, CancellationToken cancellation = default)
    {
        await _queries.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await using var connection = factory.Open();
            const string session = "receiver-capabilities";
            await connection.SendAsync(session, "hello", null, timeout.Token).ConfigureAwait(false);
            var hello = await connection.ReadAsync(timeout.Token).ConfigureAwait(false) ?? throw new IOException(L.Get("The engine did not return hello."));
            return await QueryConnectionAsync(connection, session, hello, receiver, timeout.Token).ConfigureAwait(false);
        }
        finally { _queries.Release(); }
    }
    public static async Task<ReceiverCapabilitySet> QueryConnectionAsync(IEngineConnection connection, string session, JsonElement hello, Receiver receiver, CancellationToken cancellation)
    {
        // Older verification engines can still run fixed-rate profiles, but expose no selectable capabilities.
        if (!EngineHandshake.HasCapability(hello, "receiver_capabilities")) return ReceiverCapabilitySet.Unknown;
        EngineHandshake.RequireCapabilities(hello, "receiver_capabilities");
        var peers = new List<object>();
        foreach (var member in receiver.Peers)
        {
            var addresses = await Dns.GetHostAddressesAsync(member.Address, cancellation).ConfigureAwait(false);
            var host = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? throw new IOException(L.Format("Cannot resolve IPv4 address: {0}", member.Address));
            peers.Add(new { receiver_id = member.Id, host, port = member.Port, codecs = member.Codecs.Select(c => (int)c).ToArray(), profile = new { features = member.Features, follow_receiver_clock = member.FollowReceiverClock } });
        }
        await connection.SendAsync(session, "get_receiver_capabilities", new { peers }, cancellation).ConfigureAwait(false);
        var response = await connection.ReadAsync(cancellation).ConfigureAwait(false) ?? throw new IOException(L.Get("Could not query receiver capabilities."));
        if (response.Number("version") != 1 || response.Text("session_id") != session || response.Text("event") != "receiver_capabilities")
            throw new IOException(response.Text("message", L.Get("Could not query receiver capabilities.")));
        var members = response.GetProperty("members").Deserialize<ReceiverCapabilities[]>(AppSettings.JsonOptions) ?? [];
        var expected = receiver.Peers.ToArray();
        if (members.Length != expected.Length || expected.Any(p => members.Count(m => m.ReceiverId == p.Id && m.Port == p.Port) != 1))
            throw new IOException(L.Get("Incomplete receiver capabilities."));
        return new(members);
    }
}
