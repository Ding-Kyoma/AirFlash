using System.Text;
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
public sealed class SettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly SettingsFileSystem _files;
    private readonly object _gate = new();
    private byte[]? _original;
    private bool _preservationPending;
    private bool _unsupportedSchema;
    private string? _recoveryError;
    private readonly List<byte[]> _pendingSources = [];
    private readonly List<byte[]> _migrationSources = [];
    private readonly List<(byte[] Bytes, string Path)> _backups = [];
    public string? LoadWarning { get; private set; }
    public SettingsStore(string path) : this(path, new SettingsFileSystem()) { }
    internal SettingsStore(string path, SettingsFileSystem files) { _path = path; _files = files; }
    public AppSettings Load()
    {
        lock (_gate)
        {
            byte[]? bytes = null;
            try
            {
                bytes = _files.ReadAllBytes(_path);
                if (_preservationPending) PreserveOnSave(bytes);
                var text = Decode(bytes);
                var version = ReadVersion(text);
                var json = JsonNode.Parse(text) as JsonObject ?? throw new JsonException(L.Get("Configuration must be a JSON object."));
                if (version < AppSettings.CurrentSchemaVersion)
                {
                    if (version < 2)
                    {
                        if (json["receivers"] is JsonObject receivers)
                            foreach (var value in receivers.Select(p => p.Value).OfType<JsonObject>())
                                if (value["auto_connect"]?.GetValue<bool>() == false) value["auto_connect"] = null;
                        if (json["capture_mode"]?.GetValue<string>() == "virtual")
                        {
                            json["capture_mode"] = "endpoint";
                            json["capture_endpoint"] = json["virtual_output_device"]?.DeepClone() ?? json["capture_endpoint"]?.DeepClone();
                        }
                        // Only schema 1 allowed the wider legacy buffer range.
                        ClampBuffer(json);
                        if (json["receivers"] is JsonObject items)
                            foreach (var value in items.Select(p => p.Value).OfType<JsonObject>()) ClampBuffer(value);
                    }
                    PreserveOnSave(bytes);
                    TrackMigration(bytes);
                }
                // Inherited rates became explicit per-receiver/group values in schema 4.
                // A stray legacy key is removed so it can never reach playback again.
                foreach (var key in json.Select(p => p.Key).Where(k => k.Equals("stream_sample_rate", StringComparison.OrdinalIgnoreCase)).ToArray()) json.Remove(key);
                foreach (var key in json.Select(p => p.Key).Where(k => k.Equals("schema_version", StringComparison.OrdinalIgnoreCase)).ToArray()) json.Remove(key);
                json["schema_version"] = AppSettings.CurrentSchemaVersion;
                var settings = json.Deserialize<AppSettings>(AppSettings.JsonOptions) ?? throw new JsonException(L.Get("Empty configuration."));
                settings.SoftwareGroups ??= [];
                settings.SoftwareGroups = new(settings.SoftwareGroups.Where(group => group is not null));
                settings.Receivers ??= [];
                settings.Receivers = settings.Receivers.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value);
                settings.ManualReceivers ??= [];
                settings.ReceiverAliases ??= [];
                settings.ReceiverAliases = settings.ReceiverAliases.Where(p => ReceiverIdentity.IsBroadcast(p.Key) && !string.IsNullOrWhiteSpace(p.Value) && ReceiverIdentity.IsBroadcast(p.Value))
                    .GroupBy(p => ReceiverIdentity.Normalize(p.Key)).ToDictionary(g => g.Key, g => ReceiverIdentity.Normalize(g.First().Value), StringComparer.Ordinal);
                if (settings.Validate() is { } error) throw new JsonException(L.Format("Configuration contains invalid values: {0}", error));
                _unsupportedSchema = false;
                UpdateWarning();
                return settings;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // An absent file does not discharge a previous unreadable-file obligation.
                UpdateWarning();
                return new();
            }
            catch (NotSupportedException) { _unsupportedSchema = true; throw; }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
            {
                PreserveOnSave(bytes);
                _recoveryError = error.Message;
                UpdateWarning();
                return new();
            }
        }
    }
    private void PreserveOnSave(byte[]? bytes)
    {
        _preservationPending = true;
        if (bytes is null) return;
        _original ??= bytes;
        if (!_pendingSources.Any(source => source.AsSpan().SequenceEqual(bytes))) _pendingSources.Add(bytes);
    }
    private void TrackMigration(byte[] bytes)
    {
        if (!_migrationSources.Any(source => source.AsSpan().SequenceEqual(bytes))) _migrationSources.Add(bytes);
    }
    private void UpdateWarning()
    {
        if (_recoveryError is null) return;
        LoadWarning = L.Format(_original is null
            ? "Could not read the previous configuration: {0} Saving is blocked until the original can be read and preserved."
            : "Could not read the previous configuration: {0} The captured original requires a recovery backup before replacement.", _recoveryError);
    }
    private static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    private static int ReadVersion(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(L.Get("Configuration must be a JSON object."));
        var versions = document.RootElement.EnumerateObject().Where(p => p.Name.Equals("schema_version", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).ToArray();
        // Never hide a future schema behind another spelling or a duplicate property.
        if (versions.Any(IsFutureVersion))
            throw new NotSupportedException(L.Get("This configuration is from a newer version. Update the app first."));
        if (versions.Length > 1) throw new JsonException(L.Get("Configuration contains ambiguous schema version fields."));
        if (versions.Length == 0 || versions[0].ValueKind == JsonValueKind.Null) return 1;
        if (versions[0].ValueKind != JsonValueKind.Number || !versions[0].TryGetInt32(out var version)) throw new JsonException(L.Get("Configuration schema version must be an integer."));
        return version;
    }
    private static bool IsFutureVersion(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) return false;
        var token = value.GetRawText();
        if (!token.All(char.IsAsciiDigit)) return false;
        // Compare positive integer tokens without a bounded numeric conversion so a
        // future schema cannot become recoverable through overflow.
        var current = AppSettings.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return token.Length > current.Length || token.Length == current.Length && string.CompareOrdinal(token, current) > 0;
    }
    private static void ClampBuffer(JsonObject json)
    {
        if (json["custom_buffer_ms"] is JsonValue value && value.TryGetValue<int>(out var ms)) json["custom_buffer_ms"] = Math.Clamp(ms, 0, 2000);
    }
    public void Save(AppSettings settings) { lock (_gate) SaveLocked(settings); }
    private void SaveLocked(AppSettings settings)
    {
        if (_unsupportedSchema) throw new NotSupportedException(L.Get("This configuration is from a newer version. Update the app first."));
        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion) throw new NotSupportedException(L.Get("This configuration is from a newer version. Update the app first."));
        var saved = settings.Clone();
        saved.SchemaVersion = AppSettings.CurrentSchemaVersion;
        if (saved.Extra is { } extra)
            foreach (var key in extra.Keys.Where(k => k.Equals("stream_sample_rate", StringComparison.OrdinalIgnoreCase)).ToArray()) extra.Remove(key);
        _files.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        if (_preservationPending)
        {
            byte[]? current = null;
            try { current = _files.ReadAllBytes(_path); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            if (current is not null)
            {
                // Keep every captured revision if preservation or replacement later fails.
                PreserveOnSave(current);
                // A previously unreadable file may turn out to belong to a newer app.
                try
                {
                    if (ReadVersion(Decode(current)) < AppSettings.CurrentSchemaVersion) TrackMigration(current);
                }
                catch (NotSupportedException) { _unsupportedSchema = true; throw; }
                catch (JsonException) { }
            }
            if (_original is null && current is null) throw new IOException(L.Get("The previous configuration could not be preserved. Saving has been blocked."));
            foreach (var source in _pendingSources) PreserveBytes(source);
        }
        foreach (var source in _migrationSources) PreserveMigrationBytes(source);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _files.WriteNewBytes(temp, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(saved, AppSettings.JsonOptions)));
            try { _files.Replace(temp, _path); }
            catch (FileNotFoundException) { _files.Move(temp, _path); }
        }
        catch
        {
            try { _files.Delete(temp); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
        if (_preservationPending && _recoveryError is not null)
            LoadWarning = L.Format("The previous configuration was preserved in a recovery backup: {0}", string.Join(", ", _backups.Select(b => b.Path).Distinct()));
        _preservationPending = false; _original = null; _recoveryError = null;
        _pendingSources.Clear(); _migrationSources.Clear(); _backups.Clear();
    }
    private void PreserveMigrationBytes(byte[] bytes)
    {
        foreach (var suffix in new[] { ".pre-v3.bak", ".pre-v4.bak" })
        {
            var target = _path + suffix;
            if (File.Exists(target)) continue;
            _files.WriteNewBytes(target, bytes);
        }
    }
    private void PreserveBytes(byte[] bytes)
    {
        foreach (var backup in _backups.Where(b => b.Bytes.AsSpan().SequenceEqual(bytes)).ToArray())
        {
            try { if (_files.ReadAllBytes(backup.Path).AsSpan().SequenceEqual(bytes)) return; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            _backups.Remove(backup);
        }
        var target = _path + ".pre-wpf.bak";
        try
        {
            if (_files.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) { _backups.Add((bytes, target)); return; }
            target = _path + ".pre-wpf." + Guid.NewGuid().ToString("N") + ".bak";
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        _files.WriteNewBytes(target, bytes);
        if (!_files.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) throw new IOException(L.Get("Could not verify the configuration backup."));
        _backups.Add((bytes, target));
    }
    public Task SaveReceiverIdentityAsync(AppSettings settings, bool backup) => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_unsupportedSchema) throw new NotSupportedException(L.Get("This configuration is from a newer version. Update the app first."));
            if (backup)
            {
                var target = _path + ".pre-receiver-identity.bak";
                try { _files.ReadAllBytes(target); }
                catch (UnauthorizedAccessException error) when (Directory.Exists(target))
                {
                    // Preserve the existing identity-backup contract for a directory collision.
                    throw new IOException(error.Message, error);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    byte[]? bytes = null;
                    try { bytes = _files.ReadAllBytes(_path); }
                    catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException) { }
                    if (bytes is not null)
                    {
                        if (_preservationPending)
                        {
                            PreserveOnSave(bytes);
                            try
                            {
                                if (ReadVersion(Decode(bytes)) < AppSettings.CurrentSchemaVersion) TrackMigration(bytes);
                            }
                            catch (NotSupportedException) { _unsupportedSchema = true; throw; }
                            catch (JsonException) { }
                        }
                        _files.WriteNewBytes(target, bytes);
                    }
                }
            }
            SaveLocked(settings);
        }
    });
    public Task ResetAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            var config = Path.GetFullPath(_path);
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
            _files.Delete(config);
            _preservationPending = false; _original = null; _recoveryError = null; _unsupportedSchema = false;
            _pendingSources.Clear(); _migrationSources.Clear(); _backups.Clear();
            LoadWarning = null;
        }
    });
}

// Narrow file boundary for deterministic access/backup/replacement failures.
internal class SettingsFileSystem
{
    internal virtual byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    internal virtual void CreateDirectory(string path) => Directory.CreateDirectory(path);
    internal virtual void WriteNewBytes(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); }
    internal virtual void Replace(string source, string destination) => File.Replace(source, destination, null);
    internal virtual void Move(string source, string destination) => File.Move(source, destination);
    internal virtual void Delete(string path) => File.Delete(path);
}
