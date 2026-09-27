using AirFlash.Core;
namespace AirFlash.App.ViewModels;
public sealed class DiscoveryAdapterOption(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    private string _label = label;
    public string Label { get => _label; set => Set(ref _label, value); }
}
