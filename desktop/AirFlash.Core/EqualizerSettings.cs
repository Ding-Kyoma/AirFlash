using System.Text.Json;
using System.Text.Json.Serialization;
namespace AirFlash.Core;

public sealed class EqualizerSettings : ObservableObject
{
    private bool _enabled;
    private double _preampDb;
    private double[] _bandGainsDb = new double[10];
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public double PreampDb { get => _preampDb; set => Set(ref _preampDb, value); }
    public double[] BandGainsDb { get => (double[])_bandGainsDb.Clone(); set => Set(ref _bandGainsDb, value is null ? [] : (double[])value.Clone()); }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    public string? Validate() => _bandGainsDb.Length != 10 || _bandGainsDb.Any(v => !ValidGain(v)) || !ValidGain(PreampDb)
        ? L.Get("Equalizer requires ten finite band gains and a preamp between -12 and +12 dB.") : null;
    private static bool ValidGain(double value) => double.IsFinite(value) && value is >= -12 and <= 12;
    public void SetBand(int index, double gain) { var bands = BandGainsDb; bands[index] = gain; BandGainsDb = bands; }
    public EqualizerSettings Clone() => new() { Enabled = Enabled, PreampDb = PreampDb, BandGainsDb = BandGainsDb, Extra = Extra?.ToDictionary(p => p.Key, p => p.Value.Clone()) };
    public void CopyFrom(EqualizerSettings source) { Enabled = source.Enabled; PreampDb = source.PreampDb; BandGainsDb = source.BandGainsDb; Extra = source.Extra?.ToDictionary(p => p.Key, p => p.Value.Clone()); }
    public object WireParameters() => new { enabled = Enabled, preamp_db = PreampDb, band_gains_db = BandGainsDb };
}

// The same RBJ coefficients and response grid are used by the native engine.
public static class EqualizerResponse
{
    public static readonly double[] Frequencies = [31.25, 62.5, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public static (double AutoAttenuation, double EffectivePreamp) Headroom(EqualizerSettings settings, int rate)
    {
        if (!settings.Enabled || settings.Validate() is not null) return (0, 0);
        var gains = settings.BandGainsDb;
        var coefficients = Frequencies.Select((frequency, index) => Coefficients(frequency, gains[index], rate)).ToArray();
        double Response(double frequency)
        {
            var w = 2 * Math.PI * frequency / rate;
            var c = Math.Cos(w); var s = Math.Sin(w); var c2 = Math.Cos(2 * w); var s2 = Math.Sin(2 * w);
            return coefficients.Sum(b =>
            {
                var nr = b[0] + b[1] * c + b[2] * c2; var ni = -b[1] * s - b[2] * s2;
                var dr = 1 + b[3] * c + b[4] * c2; var di = -b[3] * s - b[4] * s2;
                return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
            });
        }
        var peak = Math.Max(0, Math.Max(Response(0), Response(rate / 2d)));
        foreach (var frequency in Frequencies) peak = Math.Max(peak, Response(frequency));
        for (var i = 0; i < 4096; i++) peak = Math.Max(peak, Response(10 * Math.Pow(rate / 20d, i / 4095d)));
        var attenuation = -Math.Max(0, settings.PreampDb + peak);
        return (attenuation, settings.PreampDb + attenuation);
    }
    private static double[] Coefficients(double frequency, double gain, int rate)
    {
        if (gain == 0) return [1, 0, 0, 0, 0];
        var a = Math.Pow(10, gain / 40); var w = 2 * Math.PI * frequency / rate;
        var alpha = Math.Sin(w) / (2 * 1.4); var a0 = 1 + alpha / a;
        return [(1 + alpha * a) / a0, -2 * Math.Cos(w) / a0, (1 - alpha * a) / a0, -2 * Math.Cos(w) / a0, (1 - alpha / a) / a0];
    }
}
