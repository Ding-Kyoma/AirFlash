using System.Net;
using System.Net.Sockets;
namespace AirFlash.Core;

/// <summary>A receiver address could not be turned into an IPv4 address. Reported as
/// a retryable <c>network_error</c> rather than a host error.</summary>
internal sealed class NetworkFailure(string message) : IOException(message);

/// <summary>The one IPv4 resolution rule shared by session start, pairing and
/// capability queries, so every path accepts the same addresses and reports the
/// same localized failure.</summary>
internal static class ReceiverAddress
{
    /// <summary>The first IPv4 address for <paramref name="host"/>. IPv4 literals are
    /// returned as given; a failed lookup or a host without an IPv4 address throws
    /// <see cref="NetworkFailure"/>.</summary>
    public static async Task<string> ResolveIPv4Async(string host, CancellationToken cancellation)
    {
        if (IPAddress.TryParse(host, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork) return host;
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, cancellation).ConfigureAwait(false); }
        catch (SocketException error) { throw new NetworkFailure(L.Format("Cannot resolve IPv4 address: {0} ({1})", host, error.Message)); }
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? throw new NetworkFailure(L.Format("Cannot resolve IPv4 address: {0}", host));
    }
}
