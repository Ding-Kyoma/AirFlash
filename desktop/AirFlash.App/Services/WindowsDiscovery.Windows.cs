using AirFlash.Core;
namespace AirFlash.App.Services;

public sealed partial class WindowsDiscovery
{
    public WindowsDiscovery(Func<IReadOnlyList<DiscoveryAdapter>>? adapters = null, Action<string>? log = null, Func<bool>? dnsAvailable = null)
        : this(new ResolveProcessDnsServiceApi(new NativeDnsServiceApi(), ResolveChildFactory.ForApplication()), TimeProvider.System, adapters ?? NetworkAdapterCatalog.List, log ?? AppPaths.Log,
            enableInfrastructure: true, dnsAvailable: dnsAvailable ?? (() => WindowsCapabilities.Current.DnsSdAvailable)) { }
}
