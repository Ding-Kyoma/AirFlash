using System.Net;

namespace AirFlash.Core;

public static class ReceiverIdentity
{
    public static string Normalize(string identity)
    {
        var text = identity.Trim();
        var mac = text.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return mac.Length == 12 && mac.All(Uri.IsHexDigit) ? mac.ToLowerInvariant() : text;
    }
    public static string Endpoint(string address, int port)
    {
        var host = IPAddress.TryParse(address, out var ip) ? ip.ToString() : address.Trim().ToLowerInvariant();
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }
    public static bool TryEndpoint(string value, out string address, out int port)
    {
        address = ""; port = 0;
        if (value.StartsWith("auto-endpoint:", StringComparison.Ordinal)) value = value["auto-endpoint:".Length..];
        var separator = value.LastIndexOf(':');
        if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out port) || port is < 1 or > 65535) return false;
        var host = value[..separator].Trim('[', ']');
        if (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns) return false;
        address = host; return true;
    }
    public static bool IsBroadcast(string id)
    {
        var normalized = Normalize(id);
        return normalized.Length > 0 && !normalized.StartsWith("stereo:", StringComparison.Ordinal)
            && !normalized.StartsWith("endpoint:", StringComparison.Ordinal) && !TryEndpoint(normalized, out _, out _);
    }
    public static string Resolve(string id, IReadOnlyDictionary<string, string> aliases)
    {
        var current = Normalize(id); var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(current) && aliases.TryGetValue(current, out var next))
        {
            next = Normalize(next);
            if (next == current) return current;
            if (!IsBroadcast(next)) return Normalize(id);
            current = next;
        }
        return seen.Contains(current) && aliases.ContainsKey(current) ? Normalize(id) : current;
    }
}
