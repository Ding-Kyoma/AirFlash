using System.Text.Json;
using System.Text.Json.Nodes;
namespace AirFlash.Core;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    Task SaveAsync(AppSettings settings) => Task.Run(() => Save(settings));
}
public sealed class SettingsStore(string path) : ISettingsStore
{
    private string? _migrationSource;
    public string? LoadWarning { get; private set; }
    public AppSettings Load()
    {
        if (!File.Exists(path)) return new();
        try
        {
            var text = File.ReadAllText(path);
            var json = JsonNode.Parse(text) as JsonObject ?? throw new JsonException(L.Get("Configuration must be a JSON object."));
            var version = json["schema_version"]?.GetValue<int>() ?? 1;
            if (version > 2) throw new NotSupportedException(L.Get("This configuration is from a newer version. Update the app first."));
            if (version < 2)
            {
                _migrationSource = text;
                if (json["receivers"] is JsonObject receivers)
                    foreach (var value in receivers.Select(p => p.Value).OfType<JsonObject>())
                        if (value["auto_connect"]?.GetValue<bool>() == false) value["auto_connect"] = null;
                if (json["capture_mode"]?.GetValue<string>() == "virtual")
                {
                    json["capture_mode"] = "endpoint";
                    json["capture_endpoint"] = json["virtual_output_device"]?.DeepClone() ?? json["capture_endpoint"]?.DeepClone();
                }
                json["schema_version"] = 2;
                // Old versions allowed a wider legacy buffer range; native targets are 0–2000 ms.
                ClampBuffer(json);
                if (json["receivers"] is JsonObject items)
                    foreach (var value in items.Select(p => p.Value).OfType<JsonObject>()) ClampBuffer(value);
            }
            var settings = json.Deserialize<AppSettings>(AppSettings.JsonOptions) ?? throw new JsonException(L.Get("Empty configuration."));
            settings.Receivers ??= [];
            settings.Receivers = settings.Receivers.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
            settings.ManualReceivers ??= [];
            return settings;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LoadWarning = L.Format("Could not read the previous configuration: {0} The original will be backed up.", error.Message);
            if (File.Exists(path)) _migrationSource = File.ReadAllText(path);
            return new();
        }
    }
    private static void ClampBuffer(JsonObject json)
    {
        if (json["custom_buffer_ms"] is JsonValue value && value.TryGetValue<int>(out var ms)) json["custom_buffer_ms"] = Math.Clamp(ms, 0, 2000);
    }
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (_migrationSource is not null)
        {
            var backup = path + ".pre-wpf.bak";
            if (!File.Exists(backup)) File.WriteAllText(backup, _migrationSource);
            _migrationSource = null;
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, AppSettings.JsonOptions));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
