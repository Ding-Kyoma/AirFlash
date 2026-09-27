using System.Net.Sockets;
using System.Net.NetworkInformation;
using AirFlash.Core;
namespace AirFlash.App.Services;

public static class NetworkAdapterCatalog
{
    public static IReadOnlyList<DiscoveryAdapter> List()
    {
        var adapters = new List<DiscoveryAdapter>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = nic.GetIPProperties();
                var addresses = properties.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).ToArray();
                // Keep disconnected adapters visible: a saved selection can then explain why discovery is paused.
                adapters.Add(new(nic.Id, nic.Name, nic.Description, string.Join(", ", addresses),
                    nic.OperationalStatus == OperationalStatus.Up, (uint)(properties.GetIPv4Properties()?.Index ?? 0)));
            }
            catch (NetworkInformationException) { adapters.Add(new(nic.Id, nic.Name, nic.Description, "", false, 0)); }
        }
        return adapters.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
