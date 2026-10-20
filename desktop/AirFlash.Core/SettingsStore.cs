using System.Text.Json;
using System.Text.Json.Nodes;
namespace AirFlash.Core;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    Task SaveAsync(AppSettings settings) => Task.Run(() => Save(settings));
    Task SaveReceiverIdentityAsync(AppSettings settings, bool backup) => SaveAsync(settings);
    Task ResetAsync() => throw new NotSupportedException("This settings store does not support reset.");
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
            if (version > 4) throw new NotSupportedException(L.Get("This configuration is from a newer version. Update the app first."));
            if (version < 4) _migrationSource = text;
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
            // An inherited rate becomes automatic; explicit receiver/group rates remain intact.
            foreach (var key in json.Select(p => p.Key).Where(k => k.Equals("stream_sample_rate", StringComparison.OrdinalIgnoreCase)).ToArray()) json.Remove(key);
            json["schema_version"] = 4;
            var settings = json.Deserialize<AppSettings>(AppSettings.JsonOptions) ?? throw new JsonException(L.Get("Empty configuration."));
            settings.SoftwareGroups ??= [];
            settings.SoftwareGroups = new(settings.SoftwareGroups.Where(group => group is not null));
            settings.Receivers ??= [];
            settings.Receivers = settings.Receivers.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
            settings.ManualReceivers ??= [];
            settings.ReceiverAliases ??= [];
            settings.ReceiverAliases = settings.ReceiverAliases.Where(p => ReceiverIdentity.IsBroadcast(p.Key) && !string.IsNullOrWhiteSpace(p.Value) && ReceiverIdentity.IsBroadcast(p.Value))
                .GroupBy(p => ReceiverIdentity.Normalize(p.Key)).ToDictionary(g => g.Key, g => ReceiverIdentity.Normalize(g.First().Value), StringComparer.Ordinal);
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
        if (settings.SchemaVersion > 4) throw new NotSupportedException(L.Get("This configuration is from a newer version. Update the app first."));
        var saved = settings.Clone();
        saved.SchemaVersion = 4;
        if (saved.Extra is { } extra)
            foreach (var key in extra.Keys.Where(k => k.Equals("stream_sample_rate", StringComparison.OrdinalIgnoreCase)).ToArray()) extra.Remove(key);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (_migrationSource is not null)
        {
            var backup = path + ".pre-wpf.bak";
            if (!File.Exists(backup)) File.WriteAllText(backup, _migrationSource);
            if (!File.Exists(path + ".pre-v3.bak")) File.WriteAllText(path + ".pre-v3.bak", _migrationSource);
            if (!File.Exists(path + ".pre-v4.bak")) File.WriteAllText(path + ".pre-v4.bak", _migrationSource);
            _migrationSource = null;
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, AppSettings.JsonOptions));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Task SaveReceiverIdentityAsync(AppSettings settings, bool backup) => Task.Run(() =>
    {
        if (backup && File.Exists(path) && !File.Exists(path + ".pre-receiver-identity.bak")) File.Copy(path, path + ".pre-receiver-identity.bak");
        Save(settings);
    });
    public Task ResetAsync() => Task.Run(() =>
    {
        var config = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(config)!;
        var credentials = Path.GetFullPath(Path.Combine(root, "native-credentials"));
        if (!credentials.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid credentials path.");
        if (Directory.Exists(credentials))
        {
            var directories = new Stack<string>(); directories.Push(credentials);
            while (directories.TryPop(out var directory))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException(L.Get("Cannot reset pairing data through a linked directory."));
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(L.Get("Cannot reset pairing data through a linked directory."));
                    if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                }
            }
            Directory.Delete(credentials, true);
        }
        File.Delete(config);
        _migrationSource = null;
        LoadWarning = null;
    });
}
