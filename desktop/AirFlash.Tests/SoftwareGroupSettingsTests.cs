using System.Text;
using System.Text.Json;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

// Invalid software-group shapes must use controlled recovery at both startup loads.
// Temporary files preserve the original encoding and unknown-field contract.
public sealed class SoftwareGroupSettingsTests
{
    [Theory]
    [InlineData("utf8-bom", "null")]
    [InlineData("utf16-le-bom", "null")]
    [InlineData("utf8-bom", "blank")]
    [InlineData("utf16-le-bom", "blank")]
    [InlineData("utf8-bom", "bad-prefix")]
    [InlineData("utf16-le-bom", "bad-prefix")]
    [InlineData("utf8-bom", "whitespace")]
    [InlineData("utf16-le-bom", "whitespace")]
    [InlineData("utf8-bom", "null-name")]
    [InlineData("utf16-le-bom", "null-name")]
    [InlineData("utf8-bom", "null-rate")]
    [InlineData("utf16-le-bom", "null-rate")]
    [InlineData("utf8-bom", "null-left")]
    [InlineData("utf16-le-bom", "null-left")]
    [InlineData("utf8-bom", "null-right")]
    [InlineData("utf16-le-bom", "null-right")]
    public void InvalidSoftwareGroupRecoversAcrossBothLoadsAndPreservesOriginalBytes(string encodingName, string shape)
    {
        using var fixture = new TemporaryStore();
        string? id = shape switch
        {
            "null" => null,
            "blank" => "",
            "whitespace" => " \t",
            "bad-prefix" => "stereo:invalid-software-group",
            "null-name" or "null-rate" or "null-left" or "null-right" => "software:synthetic-group",
            _ => throw new ArgumentException(shape)
        };
        var original = Encode(Configuration(id,
            name: shape == "null-name" ? null : "Synthetic group",
            rate: shape == "null-rate" ? null : "44100",
            left: shape == "null-left" ? null : "synthetic-left",
            right: shape == "null-right" ? null : "synthetic-right"), encodingName);
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);

        // Production uses the same store for shell preferences and AppViewModel.
        AssertSafeDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        AssertSafeDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.False(File.Exists(fixture.Path + ".pre-wpf.bak"));

        store.Save(new AppSettings { MasterVolume = 29 });
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        var saved = new SettingsStore(fixture.Path).Load();
        Assert.Null(saved.Validate());
        Assert.Equal(29, saved.MasterVolume);
        Assert.Empty(saved.SoftwareGroups);
        Assert.False(saved.AutoConnectOnDiscover);
        Assert.False(saved.StartAtLogin);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le-bom")]
    public void OrdinarySoftwareGroupRemainsValidAcrossBothLoadsAndSave(string encodingName)
    {
        using var fixture = new TemporaryStore();
        var original = Encode(Configuration("software:synthetic-group"), encodingName);
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        foreach (var settings in new[] { store.Load(), store.Load() })
        {
            Assert.Null(settings.Validate());
            Assert.Null(store.LoadWarning);
            var group = Assert.Single(settings.SoftwareGroups);
            Assert.Equal("software:synthetic-group", group.Id);
            Assert.Equal("44100", group.SampleRate);
            Assert.True(settings.AutoConnectOnDiscover);
            Assert.True(settings.StartAtLogin);
            Assert.Equal("retained", settings.Extra!["synthetic_extension"].GetString());
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        var edited = store.Load(); edited.MasterVolume = 31; store.Save(edited);
        var saved = new SettingsStore(fixture.Path).Load();
        Assert.Null(saved.Validate());
        Assert.Equal(31, saved.MasterVolume);
        Assert.Equal("software:synthetic-group", Assert.Single(saved.SoftwareGroups).Id);
        Assert.Equal("retained", saved.Extra!["synthetic_extension"].GetString());
        Assert.False(File.Exists(fixture.Path + ".pre-wpf.bak"));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le-bom")]
    public void LegacySoftwareGroupRemainsPendingAcrossLoadsAndSaveUntilMigrationCompletes(string encodingName)
    {
        using var fixture = new TemporaryStore();
        var legacy = JsonSerializer.Deserialize<AppSettings>(
            Configuration("software:synthetic-group", rate: "48000", schema: 4), AppSettings.JsonOptions)!;
        legacy.MasterVolume = 37;
        legacy.LastReceiverId = "software:synthetic-group";
        legacy.Options("synthetic-left").Volume = 28;
        legacy.Options("synthetic-left").LatencyMode = "custom";
        legacy.Options("synthetic-left").CustomBufferMs = 375;
        legacy.Options("synthetic-right").Volume = 61;
        legacy.Options("synthetic-right").Codec = "alac";
        var legacyGroup = Assert.Single(legacy.SoftwareGroups);
        legacyGroup.LeftOffsetMs = -80;
        legacyGroup.RightOffsetMs = 25;
        var original = Encode(JsonSerializer.Serialize(legacy, AppSettings.JsonOptions), encodingName);
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);

        var first = store.Load();
        Assert.True(store.MixedGroupMigrationPending);
        AssertLegacyValues(first, 37);
        var second = store.Load();
        Assert.True(store.MixedGroupMigrationPending);
        AssertLegacyValues(second, 37);
        Assert.Null(store.LoadWarning);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.bak"));

        second.MasterVolume = 39;
        store.Save(second);
        Assert.True(store.MixedGroupMigrationPending);
        AssertPersistedVersion(fixture.Path, 4, 0);
        foreach (var suffix in new[] { ".pre-wpf.bak", ".pre-v3.bak", ".pre-v4.bak" })
            Assert.Equal(original, File.ReadAllBytes(fixture.Path + suffix));
        Assert.False(File.Exists(fixture.Path + ".pre-v5.bak"));
        var intermediate = File.ReadAllBytes(fixture.Path);

        // A new store proves that saving before the catalog pass cannot let a
        // later process skip the still-pending migration.
        var restarted = new SettingsStore(fixture.Path);
        AssertLegacyValues(restarted.Load(), 39);
        Assert.True(restarted.MixedGroupMigrationPending);
        var pending = restarted.Load();
        AssertLegacyValues(pending, 39);
        Assert.True(restarted.MixedGroupMigrationPending);
        Assert.Null(restarted.LoadWarning);
        Assert.Equal(intermediate, File.ReadAllBytes(fixture.Path));
        restarted.CompleteMixedGroupMigration(pending);

        Assert.False(restarted.MixedGroupMigrationPending);
        AssertPersistedVersion(fixture.Path, AppSettings.CurrentSchemaVersion, AppSettings.CurrentSchemaVersion);
        // The original recovery/migration backups remain byte-exact. The v5
        // backup belongs to the intermediate schema-4 revision just reloaded.
        foreach (var suffix in new[] { ".pre-wpf.bak", ".pre-v3.bak", ".pre-v4.bak" })
            Assert.Equal(original, File.ReadAllBytes(fixture.Path + suffix));
        Assert.Equal(intermediate, File.ReadAllBytes(fixture.Path + ".pre-v5.bak"));
        Assert.Contains(Directory.GetFiles(fixture.DirectoryPath, "*.pre-wpf.*.bak"),
            path => File.ReadAllBytes(path).AsSpan().SequenceEqual(intermediate));
        var completedStore = new SettingsStore(fixture.Path);
        var completed = completedStore.Load();
        AssertLegacyValues(completed, 39);
        Assert.False(completedStore.MixedGroupMigrationPending);
        Assert.Null(completedStore.LoadWarning);
        Assert.Equal(AppSettings.CurrentSchemaVersion, Assert.Single(completed.SoftwareGroups).MixedInvariantVersion);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    private static void AssertLegacyValues(AppSettings settings, int volume)
    {
        Assert.Null(settings.Validate());
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(volume, settings.MasterVolume);
        Assert.Equal("software:synthetic-group", settings.LastReceiverId);
        Assert.True(settings.AutoConnectOnDiscover);
        Assert.True(settings.StartAtLogin);
        Assert.Equal("retained", settings.Extra!["synthetic_extension"].GetString());
        var group = Assert.Single(settings.SoftwareGroups);
        Assert.Equal("software:synthetic-group", group.Id);
        Assert.Equal("Synthetic group", group.Name);
        Assert.Equal("synthetic-left", group.LeftId);
        Assert.Equal("synthetic-right", group.RightId);
        Assert.Equal("48000", group.SampleRate);
        Assert.Equal(-80, group.LeftOffsetMs);
        Assert.Equal(25, group.RightOffsetMs);
        Assert.Equal(28, settings.ReadOptions("synthetic-left").Volume);
        Assert.Equal("custom", settings.ReadOptions("synthetic-left").LatencyMode);
        Assert.Equal(375, settings.ReadOptions("synthetic-left").CustomBufferMs);
        Assert.Equal(61, settings.ReadOptions("synthetic-right").Volume);
        Assert.Equal("alac", settings.ReadOptions("synthetic-right").Codec);
    }

    private static void AssertPersistedVersion(string path, int schema, int invariant)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(schema, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(invariant, document.RootElement.GetProperty("software_groups")[0].GetProperty("mixed_invariant_version").GetInt32());
    }
    [Fact]
    public void NullSoftwareGroupIdentityReturnsAValidationDiagnostic()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(Configuration(null), AppSettings.JsonOptions)!;
        Assert.False(string.IsNullOrWhiteSpace(settings.Validate()));
    }

    [Theory]
    [InlineData("\"multi_room\"", "null")]
    [InlineData("\"multi_room\"", "[null]")]
    [InlineData("\"unknown\"", "[]")]
    [InlineData("null", "[]")]
    public void MalformedGroupModeOrMembersUseSettingsRecovery(string mode, string members)
    {
        using var fixture = new TemporaryStore();
        var original = Encode($$"""
            {
              "schema_version": {{AppSettings.CurrentSchemaVersion}},
              "software_groups": [{
                "id":"software:synthetic", "left_id":"a", "right_id":"b",
                "group_mode":{{mode}}, "members":{{members}}
              }]
            }
            """, "utf8-bom");
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);

        AssertSafeDefaults(store.Load());
        AssertSafeDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        store.Save(new AppSettings { MasterVolume = 29 });

        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        Assert.Equal(29, new SettingsStore(fixture.Path).Load().MasterVolume);
    }

    private static string Configuration(string? id, string? name = "Synthetic group", string? rate = "44100",
        string? left = "synthetic-left", string? right = "synthetic-right", int schema = AppSettings.CurrentSchemaVersion) => JsonSerializer.Serialize(new
    {
        schema_version = schema,
        auto_connect_on_discover = true,
        start_at_login = true,
        synthetic_extension = "retained",
        software_groups = new[] { new { id, name, left_id = left, right_id = right, sample_rate = rate } }
    });

    private static byte[] Encode(string text, string name)
    {
        Encoding encoding = name switch
        {
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le-bom" => new UnicodeEncoding(false, true),
            _ => throw new ArgumentException(name)
        };
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    private static void AssertSafeDefaults(AppSettings settings)
    {
        Assert.Null(settings.Validate());
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(100, settings.MasterVolume);
        Assert.False(settings.AutoConnectOnDiscover);
        Assert.False(settings.StartAtLogin);
        Assert.Empty(settings.SoftwareGroups);
        Assert.Empty(settings.ManualReceivers);
        Assert.Empty(settings.Receivers);
    }

    private sealed class TemporaryStore : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airflash-software-group-settings", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(DirectoryPath, "config.json");
        public TemporaryStore() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
