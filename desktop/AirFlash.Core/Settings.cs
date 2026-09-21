using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
namespace AirFlash.Core;

public sealed class ReceiverOptions : ObservableObject
{
    public void CopyFrom(ReceiverOptions source)
    {
        AutoConnect = source.AutoConnect; Hidden = source.Hidden; Volume = source.Volume;
        LatencyMode = source.LatencyMode; CustomBufferMs = source.CustomBufferMs; StandbySeconds = source.StandbySeconds;
    }
    private bool? _AutoConnect = null;
    public bool? AutoConnect { get => _AutoConnect; set => Set(ref _AutoConnect, value); }
    private bool _Hidden = false;
    public bool Hidden { get => _Hidden; set => Set(ref _Hidden, value); }
    private int? _Volume = null;
    public int? Volume { get => _Volume; set => Set(ref _Volume, value); }
    private string? _LatencyMode = null;
    public string? LatencyMode { get => _LatencyMode; set => Set(ref _LatencyMode, value); }
    private int? _CustomBufferMs = null;
    public int? CustomBufferMs { get => _CustomBufferMs; set => Set(ref _CustomBufferMs, value); }
    private double? _StandbySeconds = null;
    public double? StandbySeconds { get => _StandbySeconds; set => Set(ref _StandbySeconds, value); }
}
public sealed record ManualReceiver(string Id, string Name, string Host, int Port);
public sealed class AppSettings : ObservableObject
{
    public int SchemaVersion { get; set; } = 2;
    private bool _StartAtLogin = false;
    public bool StartAtLogin { get => _StartAtLogin; set => Set(ref _StartAtLogin, value); }
    private bool _MuteWhileStreaming = true;
    public bool MuteWhileStreaming { get => _MuteWhileStreaming; set => Set(ref _MuteWhileStreaming, value); }
    private bool _AutoConnectOnDiscover = false;
    public bool AutoConnectOnDiscover { get => _AutoConnectOnDiscover; set => Set(ref _AutoConnectOnDiscover, value); }
    private bool _ForceReconnect = false;
    public bool ForceReconnect { get => _ForceReconnect; set => Set(ref _ForceReconnect, value); }
    private int _MaxReconnectAttempts = 5;
    public int MaxReconnectAttempts { get => _MaxReconnectAttempts; set => Set(ref _MaxReconnectAttempts, value); }
    // Retired layout preferences always use the compact panel defaults.
    public bool ShowWiderVolume { get => false; set { } }
    public bool ShowStreamingModes { get => true; set { } }
    public bool ShowMasterControl { get => true; set { } }
    private string _UiLanguage = "system";
    public string UiLanguage { get => _UiLanguage; set => Set(ref _UiLanguage, value); }
    private string _Theme = "system";
    public string Theme { get => _Theme; set => Set(ref _Theme, value); }
    private string _StreamSampleRate = "44100";
    public string StreamSampleRate { get => _StreamSampleRate; set => Set(ref _StreamSampleRate, value); }
    private string _LatencyMode = "normal";
    public string LatencyMode { get => _LatencyMode; set => Set(ref _LatencyMode, value); }
    private int _CustomBufferMs = 1000;
    public int CustomBufferMs { get => _CustomBufferMs; set => Set(ref _CustomBufferMs, value); }
    private bool _StandbyEnabled = false;
    public bool StandbyEnabled { get => _StandbyEnabled; set => Set(ref _StandbyEnabled, value); }
    private double _StandbySilenceSeconds = 10;
    public double StandbySilenceSeconds { get => _StandbySilenceSeconds; set => Set(ref _StandbySilenceSeconds, value); }
    private string _CaptureMode = "loopback";
    public string CaptureMode { get => _CaptureMode; set => Set(ref _CaptureMode, value); }
    private string? _CaptureEndpoint = null;
    public string? CaptureEndpoint { get => _CaptureEndpoint; set => Set(ref _CaptureEndpoint, value); }
    public void RefreshCaptureEndpoint() => Notify(nameof(CaptureEndpoint));
    private int _MasterVolume = 100;
    public int MasterVolume { get => _MasterVolume; set => Set(ref _MasterVolume, value); }
    public string? LastReceiverId { get; set; }
    public Dictionary<string, ReceiverOptions> Receivers { get; set; } = new(StringComparer.Ordinal);
    public ObservableCollection<ManualReceiver> ManualReceivers { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    public ReceiverOptions Options(string id)
    {
        if (!Receivers.TryGetValue(id, out var options)) Receivers[id] = options = new();
        return options;
    }
    public ReceiverOptions ReadOptions(string id) => Receivers.GetValueOrDefault(id) ?? new();
    public int Latency(string id)
    {
        var options = ReadOptions(id);
        return (options.LatencyMode ?? LatencyMode) switch { "realtime" => 120, "buffered" => 500, "custom" => Math.Clamp(options.CustomBufferMs ?? CustomBufferMs, 0, 2000), _ => 200 };
    }
    public double Gain(string id) => Math.Clamp(MasterVolume, 0, 100) / 100d;
    public string? EffectiveEndpoint => CaptureMode == "loopback" ? null : CaptureEndpoint;
    public string? Validate()
    {
        if (UiLanguage is not ("system" or "en" or "zh")) return L.Get("Select a valid language.");
        if (Theme is not ("system" or "dark" or "light")) return L.Get("Select a valid theme.");
        if (MasterVolume is < 0 or > 100) return L.Get("Master volume must be between 0 and 100.");
        if (MaxReconnectAttempts is < 1 or > 20) return L.Get("Retry attempts must be between 1 and 20.");
        if (!Modes.Contains(LatencyMode)) return L.Get("Select a valid latency mode.");
        if (!SampleRates.Contains(StreamSampleRate)) return L.Get("Select a valid stream sample rate.");
        if (CustomBufferMs is < 0 or > 2000) return L.Get("Custom latency must be between 0 and 2000 ms.");
        if (!double.IsFinite(StandbySilenceSeconds) || StandbySilenceSeconds is < 5 or > 300) return L.Get("Standby threshold must be between 5 and 300 seconds.");
        if (CaptureMode is not ("loopback" or "endpoint")) return L.Get("Select a valid capture mode.");
        if (CaptureMode == "endpoint" && string.IsNullOrWhiteSpace(CaptureEndpoint)) return L.Get("Select a capture endpoint.");
        foreach (var options in Receivers.Values)
        {
            if (options.Volume is < 0 or > 100) return L.Get("Device volume must be between 0 and 100.");
            if (options.LatencyMode is not null && !Modes.Contains(options.LatencyMode)) return L.Get("Invalid device latency mode.");
            if (options.CustomBufferMs is < 0 or > 2000) return L.Get("Device custom latency must be between 0 and 2000 ms.");
            if (options.StandbySeconds is { } seconds && (!double.IsFinite(seconds) || seconds is < 5 or > 300)) return L.Get("Device standby threshold must be between 5 and 300 seconds.");
        }
        if (ManualReceivers.Any(r => string.IsNullOrWhiteSpace(r.Host) || r.Port is < 1 or > 65535)) return L.Get("Manual receivers require a valid address and port (1–65535).");
        return null;
    }
    public void CopyFrom(AppSettings source)
    {
        SchemaVersion = source.SchemaVersion;
        StartAtLogin = source.StartAtLogin; MuteWhileStreaming = source.MuteWhileStreaming;
        AutoConnectOnDiscover = source.AutoConnectOnDiscover; ForceReconnect = source.ForceReconnect;
        MaxReconnectAttempts = source.MaxReconnectAttempts; UiLanguage = source.UiLanguage; Theme = source.Theme;
        StreamSampleRate = source.StreamSampleRate;
        LatencyMode = source.LatencyMode; CustomBufferMs = source.CustomBufferMs;
        StandbyEnabled = source.StandbyEnabled; StandbySilenceSeconds = source.StandbySilenceSeconds;
        CaptureMode = source.CaptureMode; CaptureEndpoint = source.CaptureEndpoint;
        MasterVolume = source.MasterVolume; LastReceiverId = source.LastReceiverId;
        Extra = source.Extra?.ToDictionary(p => p.Key, p => p.Value.Clone());
        foreach (var id in Receivers.Keys.Except(source.Receivers.Keys).ToArray()) Receivers.Remove(id);
        foreach (var (id, options) in source.Receivers) Options(id).CopyFrom(options);
        foreach (var receiver in ManualReceivers.Where(r => !source.ManualReceivers.Contains(r)).ToArray()) ManualReceivers.Remove(receiver);
        for (var i = 0; i < source.ManualReceivers.Count; i++)
        {
            var receiver = source.ManualReceivers[i];
            var index = ManualReceivers.IndexOf(receiver);
            if (index < 0) ManualReceivers.Insert(i, receiver); else if (index != i) ManualReceivers.Move(index, i);
        }
    }
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
    public static readonly string[] Modes = ["realtime", "normal", "buffered", "custom"];
    public static readonly string[] SampleRates = ["44100", "48000"];
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, PropertyNameCaseInsensitive = true, IgnoreReadOnlyProperties = true };
}
public static class SettingsMerge
{
    // Three-way, field-level merge: untouched draft fields keep the newest live value.
    public static AppSettings Merge(AppSettings baseline, AppSettings draft, AppSettings current)
    {
        var original = JsonSerializer.SerializeToNode(baseline, AppSettings.JsonOptions)!;
        var edited = JsonSerializer.SerializeToNode(draft, AppSettings.JsonOptions)!;
        var latest = JsonSerializer.SerializeToNode(current, AppSettings.JsonOptions)!;
        return Patch(original, edited, latest)!.Deserialize<AppSettings>(AppSettings.JsonOptions)!;
    }
    public static bool Equal(AppSettings left, AppSettings right) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left, AppSettings.JsonOptions), JsonSerializer.SerializeToNode(right, AppSettings.JsonOptions));
    private static JsonNode? Patch(JsonNode? original, JsonNode? edited, JsonNode? latest)
    {
        if (JsonNode.DeepEquals(original, edited)) return latest?.DeepClone();
        if (edited is JsonObject edit && original is JsonObject old)
        {
            var result = latest?.DeepClone() as JsonObject ?? (JsonObject)old.DeepClone();
            foreach (var key in old.Select(p => p.Key).Union(edit.Select(p => p.Key)))
            {
                if (JsonNode.DeepEquals(old[key], edit[key])) continue;
                if (!edit.ContainsKey(key)) { result.Remove(key); continue; }
                result[key] = Patch(old[key], edit[key], result[key]);
            }
            return result;
        }
        return edited?.DeepClone();
    }
}
