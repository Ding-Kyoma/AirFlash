using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using AirFlash.Core;
namespace AirFlash.App.ViewModels;

public sealed class EqualizerEditor : ObservableObject, IDisposable
{
    private readonly EqualizerSettings _settings;
    private readonly Func<int> _rate;
    private readonly Action _changed;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private long _responseRevision;
    private bool _disposed, _responseRunning;
    private double _attenuation, _effective;
    private static readonly string[] Names = ["Flat", "Bass boost", "Vocals", "Pop", "Rock"];
    private static readonly double[][] Curves = [
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [6, 4, 2, 0, 0, 0, 0, 0, 0, 0],
        [-3, -3, -2, 0, 2, 4, 3, 1, -1, -2], [-1, 1, 3, 4, 2, 0, -1, 1, 2, 3], [4, 3, 1, -1, -2, 0, 2, 4, 4, 3]];
    public Choice[] Presets { get; } = Names.Select((name, index) => new Choice(index.ToString(CultureInfo.InvariantCulture), L.Get(name))).Append(new("custom", L.Get("Custom"))).ToArray();
    public EqualizerBandEditor[] Bands { get; }
    public RelayCommand ResetCommand { get; }
    public bool Enabled { get => _settings.Enabled; set => _settings.Enabled = value; }
    public double PreampDb { get => _settings.PreampDb; set => _settings.PreampDb = value; }
    public string PreampLabel => Db(PreampDb);
    public string HeadroomLabel => L.Format("Automatic attenuation: {0} · Effective preamp: {1}", Db(_attenuation), Db(_effective));
    public string SelectedPreset
    {
        get { var bands = _settings.BandGainsDb; var index = Array.FindIndex(Curves, curve => curve.SequenceEqual(bands)); return index < 0 ? "custom" : index.ToString(CultureInfo.InvariantCulture); }
        set { if (int.TryParse(value, out var index) && index >= 0 && index < Curves.Length) { _settings.PreampDb = 0; _settings.BandGainsDb = Curves[index]; } }
    }
    public EqualizerEditor(EqualizerSettings settings, Func<int> rate, Action changed)
    {
        _settings = settings; _rate = rate; _changed = changed;
        string[] labels = ["31 Hz", "63 Hz", "125 Hz", "250 Hz", "500 Hz", "1 kHz", "2 kHz", "4 kHz", "8 kHz", "16 kHz"];
        Bands = labels.Select((label, index) => new EqualizerBandEditor(settings, index, label)).ToArray();
        ResetCommand = new(() => { _settings.PreampDb = 0; _settings.BandGainsDb = Curves[0]; });
        _settings.PropertyChanged += SettingsChanged;
        RefreshHeadroom();
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        Notify(nameof(Enabled)); Notify(nameof(PreampDb)); Notify(nameof(PreampLabel)); Notify(nameof(SelectedPreset));
        foreach (var band in Bands) band.Refresh();
        RefreshHeadroom(); _changed();
    }
    public void RefreshHeadroom()
    {
        ++_responseRevision;
        if (_disposed || _responseRunning) return;
        _responseRunning = true; _ = UpdateHeadroomAsync();
    }
    private async Task UpdateHeadroomAsync()
    {
        try
        {
            while (!_disposed)
            {
                await Task.Delay(50);
                if (_disposed) return;
                var revision = _responseRevision; var settings = _settings.Clone(); var rate = _rate();
                var result = await Task.Run(() => EqualizerResponse.Headroom(settings, rate));
                if (_disposed) return;
                await _dispatcher.InvokeAsync(() => { if (!_disposed && revision == _responseRevision) { _attenuation = result.AutoAttenuation; _effective = result.EffectivePreamp; Notify(nameof(HeadroomLabel)); } });
                if (revision == _responseRevision) return;
            }
        }
        finally { _responseRunning = false; }
    }
    internal static string Db(double value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture) + " dB";
    public void Dispose() { _disposed = true; ++_responseRevision; _settings.PropertyChanged -= SettingsChanged; }
}

public sealed class EqualizerBandEditor(EqualizerSettings settings, int index, string label) : ObservableObject
{
    public string Label { get; } = label;
    public string AutomationName => L.Format("Equalizer band {0}", Label);
    public double Gain { get { var bands = settings.BandGainsDb; return bands.Length == 10 ? bands[index] : 0; } set { if (settings.BandGainsDb.Length == 10) settings.SetBand(index, value); } }
    public string GainLabel => EqualizerEditor.Db(Gain);
    public void Refresh() { Notify(nameof(Gain)); Notify(nameof(GainLabel)); }
}
