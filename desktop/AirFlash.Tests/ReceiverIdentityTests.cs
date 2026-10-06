using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class ReceiverIdentityTests
{
    private static ServiceRecord Service(string type = "_airplay", string? id = "AA:BB:CC:DD:EE:01", string host = "192.0.2.10", int port = 7000, string model = "AudioAccessory6,1", string stereo = "", string key = "")
        => new(type == "_raop" ? (id ?? "AABBCCDDEE01") + "@Office._raop._tcp.local" : "Office._airplay._tcp.local", type + "._tcp.local", host, port,
            new Dictionary<string, string> { ["deviceid"] = id ?? "", ["model"] = model, ["tsid"] = stereo, ["pk"] = key, ["cn"] = "0,1,2,3" });
    [Theory]
    [InlineData(" AA:BB:CC:DD:EE:01 ", "aabbccddee01")]
    [InlineData("AA-BB-CC-DD-EE-01", "aabbccddee01")]
    [InlineData("aabbccddee01", "aabbccddee01")]
    [InlineData(" Case-Sensitive-ID ", "Case-Sensitive-ID")]
    public void NormalizesOnlyMacIdentifiers(string input, string expected) => Assert.Equal(expected, ReceiverIdentity.Normalize(input));
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("different-id")]
    [InlineData("AA-BB-CC-DD-EE-01")]
    public void SameEndpointServicesMergeInEitherOrder(string? id)
    {
        var airplay = Service(id: id); var raop = Service("_raop", "AABBCCDDEE01");
        var first = Assert.Single(ReceiverAggregator.Build([airplay, raop]));
        var second = Assert.Single(ReceiverAggregator.Build([raop, airplay]));
        Assert.Equal(first.Id, second.Id); Assert.Equal(first.Aliases, second.Aliases);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, first.Codecs);
    }
    [Fact]
    public void EmptyRaopDeviceIdUsesInstancePrefix()
    {
        var receiver = Assert.Single(ReceiverAggregator.Build([Service("_raop", null)]));
        Assert.Equal("aabbccddee01", receiver.Id);
    }
    [Theory]
    [InlineData("model")]
    [InlineData("stereo")]
    [InlineData("key")]
    public void ConflictingEndpointMetadataDoesNotMerge(string conflict)
    {
        var notices = new List<string>();
        var first = Service(model: "first", stereo: "one", key: "pk-one");
        var second = Service("_raop", "different", model: conflict == "model" ? "second" : "first", stereo: conflict == "stereo" ? "two" : "one", key: conflict == "key" ? "pk-two" : "pk-one");
        var receivers = ReceiverAggregator.Build([first, second], notices.Add);
        Assert.Equal(2, receivers.SelectMany(r => r.Peers).Count());
        Assert.Contains(notices, n => n.StartsWith("Discovery identity conflict:", StringComparison.Ordinal));
        Assert.DoesNotContain(notices, n => n.Contains("pk-one", StringComparison.Ordinal));
    }
    [Fact]
    public void EqualNamesAndDifferentEndpointsStaySeparate()
    {
        Assert.Equal(2, ReceiverAggregator.Build([Service(), Service("_raop", "different", host: "192.0.2.11")]).Count);
        Assert.Equal(2, ReceiverAggregator.Build([Service(), Service("_raop", "different", port: 7001)]).Count);
    }
    [Fact]
    public void IdentityAndEndpointLinksMergeTransitivelyInEitherOrder()
    {
        var services = new[] { Service(id: "airplay-id"), Service(id: "raop-id", host: "192.0.2.11") with { Instance = "Second._airplay._tcp.local" }, Service("_raop", "raop-id") };
        var receiver = Assert.Single(ReceiverAggregator.Build(services));
        Assert.Equal(receiver.Id, Assert.Single(ReceiverAggregator.Build(services.Reverse())).Id);
        Assert.Contains("airplay-id", receiver.Aliases); Assert.Contains("raop-id", receiver.Aliases);
    }
    [Fact]
    public void ConflictingUnidentifiedServicesKeepIndependentTransientIds()
    {
        var services = new[] { Service(id: null, model: "first"), Service("_raop", null, model: "second") with { Instance = "Office._raop._tcp.local" } };
        var records = ReceiverAggregator.Build(services);
        Assert.Equal(2, records.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        var settings = new AppSettings(); settings.Options("192.0.2.10:7000").Hidden = true;
        var catalog = ReceiverCatalog.Reconcile(records, settings);
        Assert.Equal(2, catalog.Receivers.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(catalog.Settings.ReceiverAliases); Assert.True(catalog.Settings.ReadOptions("192.0.2.10:7000").Hidden);
        Assert.All(catalog.Receivers, r => Assert.False(catalog.Settings.ReadOptions(r.Id).Hidden));
        Assert.Equal(records.Select(r => r.Id), ReceiverAggregator.Build(services.Reverse()).Select(r => r.Id));
    }
    [Fact]
    public void PublicKeysAreCaseSensitiveAndMissingModelFallsBackToRaop()
    {
        Assert.Equal(2, ReceiverAggregator.Build([Service(key: "AbCd=="), Service("_raop", "other", key: "abcd==")]).Count);
        Assert.Equal("AudioAccessory6,1", Assert.Single(ReceiverAggregator.Build([Service(model: ""), Service("_raop", "AABBCCDDEE01")])).Model);
        var legacy = Service("_raop", "AABBCCDDEE01", model: "") with { Txt = new Dictionary<string, string> { ["am"] = "AudioAccessory6,1", ["model"] = "", ["cn"] = "" } };
        var complete = Service("_raop", "AABBCCDDEE01") with { Instance = "AABBCCDDEE01@Second._raop._tcp.local" };
        var merged = Assert.Single(ReceiverAggregator.Build([legacy, complete]));
        Assert.Equal("AudioAccessory6,1", merged.Model); Assert.Equal(new byte[] { 0, 1, 2, 3 }, merged.Codecs);
    }
    [Fact]
    public void StereoMembersAreDeduplicatedBeforeCounting()
    {
        var services = new[] { Service(stereo: "pair"), Service("_raop", "different", stereo: "pair"), Service(id: "AA:BB:CC:DD:EE:02", host: "192.0.2.11", stereo: "pair") };
        var pair = Assert.Single(ReceiverAggregator.Build(services));
        Assert.Equal("stereo:pair", pair.Id); Assert.True(pair.Complete); Assert.Equal(2, pair.Members.Length);
        Assert.False(Assert.Single(ReceiverAggregator.Build(services.Take(2))).Complete);
        Assert.Equal(pair.TransportKey, Assert.Single(ReceiverAggregator.Build(services.Reverse())).TransportKey);
    }
    [Fact]
    public void StereoMemberPreferencesMigrateIndependentlyFromTheGroup()
    {
        var settings = new AppSettings();
        settings.Options("AA:BB:CC:DD:EE:01").Hidden = true;
        settings.Options("192.0.2.10:7000").Volume = 45;
        settings.Options("AA-BB-CC-DD-EE-02").StandbySeconds = 25;
        settings.Options("stereo:pair").LatencyMode = "buffered";
        var migrated = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([
            Service(stereo: "pair"), Service("_raop", "other", stereo: "pair"),
            Service(id: "AA:BB:CC:DD:EE:02", host: "192.0.2.11", stereo: "pair")]), settings);
        var group = Assert.Single(migrated.Receivers);
        Assert.Equal("stereo:pair", group.Id); Assert.True(group.Complete);
        Assert.True(migrated.Settings.ReadOptions("aabbccddee01").Hidden);
        Assert.Equal(45, migrated.Settings.ReadOptions("aabbccddee01").Volume);
        Assert.Equal(25, migrated.Settings.ReadOptions("aabbccddee02").StandbySeconds);
        Assert.False(migrated.Settings.ReadOptions(group.Id).Hidden);
        Assert.Equal("buffered", migrated.Settings.ReadOptions(group.Id).LatencyMode);
        Assert.All(group.Members, member => Assert.True(ReceiverCatalog.IsCoveredMember(member.Id, migrated.Receivers)));
        Assert.All(group.Members, member => Assert.False(ReceiverCatalog.IsCoveredMember(member.Id, [group with { Online = false }])));
        Assert.DoesNotContain("192.0.2.10:7000", migrated.Settings.Receivers.Keys);
    }
    [Fact]
    public void AliasesSurviveServiceLossRestartAndAddressChange()
    {
        var first = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service("_raop", "other-id")]), new());
        var both = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(), Service("_raop", "other-id")]), first.Settings);
        var restarted = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(host: "192.0.2.99")]), both.Settings.Clone());
        Assert.Equal("other-id", Assert.Single(both.Receivers).Id); Assert.Equal("other-id", Assert.Single(restarted.Receivers).Id);
        Assert.DoesNotContain(both.Settings.ReceiverAliases.Keys, id => ReceiverIdentity.TryEndpoint(id, out _, out _));
        Assert.True(SettingsMerge.Equal(both.Settings, ReceiverCatalog.Reconcile(both.Receivers, both.Settings).Settings));
    }
    [Fact]
    public void MigrationPreservesPreferencesAndRealHistory()
    {
        var settings = new AppSettings { LastReceiverId = "AA:BB:CC:DD:EE:01" };
        settings.Options("AA:BB:CC:DD:EE:01").Volume = 60;
        settings.Options("192.0.2.10:7000").Hidden = true;
        settings.Options("192.0.2.10:7000").AutoConnect = false;
        settings.Options("aabbccddee01").AutoConnect = true;
        settings.Options("history").StandbySeconds = 25;
        var migration = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service()]), settings);
        Assert.True(migration.NeedsBackup); Assert.Equal("aabbccddee01", migration.Settings.LastReceiverId);
        Assert.True(migration.Settings.ReadOptions("aabbccddee01").Hidden); Assert.False(migration.Settings.ReadOptions("aabbccddee01").AutoConnect);
        Assert.Equal(60, migration.Settings.ReadOptions("aabbccddee01").Volume);
        Assert.Equal(new[] { "aabbccddee01", "history" }, migration.Settings.Receivers.Keys.Order(StringComparer.Ordinal));
        Assert.True(settings.Receivers.ContainsKey("192.0.2.10:7000"));
    }
    [Theory]
    [InlineData(null, null, "a-raop", 60)]
    [InlineData(null, "z-airplay", "z-airplay", 80)]
    [InlineData("a-raop", "z-airplay", "a-raop", 60)]
    public void CanonicalAndPreferenceSelectionFollowPriorityThenOrdinalOrder(string? active, string? last, string canonical, int volume)
    {
        var settings = new AppSettings { LastReceiverId = last };
        settings.Options("z-airplay").Volume = 80; settings.Options("z-airplay").LatencyMode = "custom";
        settings.Options("a-raop").Volume = 60; settings.Options("a-raop").LatencyMode = "";
        var result = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(id: "z-airplay"), Service("_raop", "a-raop")]), settings, active);
        Assert.Equal(canonical, Assert.Single(result.Receivers).Id);
        Assert.Equal(volume, result.Settings.ReadOptions(canonical).Volume);
        Assert.Equal("custom", result.Settings.ReadOptions(canonical).LatencyMode);
    }
    [Fact]
    public void ExistingAliasIdentityWinsAndPreferencesStillUseOldIdPriority()
    {
        var settings = new AppSettings(); settings.ReceiverAliases["a-raop"] = "z-airplay"; settings.ReceiverAliases["z-airplay"] = "z-airplay";
        settings.Options("z-airplay").Volume = 80; settings.Options("a-raop").Volume = 60;
        var result = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(id: "z-airplay"), Service("_raop", "a-raop")]), settings);
        Assert.Equal("z-airplay", Assert.Single(result.Receivers).Id); Assert.Equal(60, result.Settings.ReadOptions("z-airplay").Volume);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveAndLastIdentityPriorityIncludesFormattedMacSettings(bool active)
    {
        var settings = new AppSettings { LastReceiverId = active ? "0-other" : "aabbccddee01" };
        settings.Options("AA:BB:CC:DD:EE:01").Volume = 80; settings.Options("0-other").Volume = 60;
        var result = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(), Service("_raop", "0-other")]), settings, active ? "aabbccddee01" : null);
        Assert.Equal("aabbccddee01", Assert.Single(result.Receivers).Id);
        Assert.Equal(80, result.Settings.ReadOptions("aabbccddee01").Volume);
    }
    [Fact]
    public void ManualAndAmbiguousEndpointSettingsAreNotMigrated()
    {
        var settings = new AppSettings(); settings.ManualReceivers.Add(new("192.0.2.10:7000", "Manual", "192.0.2.10", 7000));
        settings.Options("192.0.2.10:7000").Hidden = true;
        var manual = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service()]), settings);
        Assert.True(manual.Settings.Receivers.ContainsKey("192.0.2.10:7000")); Assert.False(manual.Settings.ReadOptions("aabbccddee01").Hidden);
        settings.ManualReceivers.Clear();
        var ambiguous = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(model: "one"), Service("_raop", "other", model: "two")]), settings);
        Assert.True(ambiguous.Settings.Receivers.ContainsKey("192.0.2.10:7000"));
    }
    [Fact]
    public void UnidentifiedAutomaticReceiverStaysSeparateFromManualAndCanUpgrade()
    {
        const string manualId = "192.0.2.10:7000";
        var settings = new AppSettings(); settings.ManualReceivers.Add(new(manualId, "Manual", "192.0.2.10", 7000));
        settings.Options(manualId).Hidden = true;
        var fallback = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(id: null)]), settings);
        var automatic = Assert.Single(fallback.Receivers);
        Assert.NotEqual(manualId, automatic.Id); Assert.False(fallback.Settings.ReadOptions(automatic.Id).Hidden);
        Assert.Empty(fallback.Settings.ReceiverAliases);
        fallback.Settings.Options(automatic.Id).StandbySeconds = 25;
        var upgraded = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service()]), fallback.Settings, automatic.Id);
        Assert.Equal("aabbccddee01", Assert.Single(upgraded.Receivers).Id);
        Assert.Equal(25, upgraded.Settings.ReadOptions("aabbccddee01").StandbySeconds);
        Assert.True(upgraded.Settings.ReadOptions(manualId).Hidden); Assert.False(upgraded.Settings.ReadOptions("aabbccddee01").Hidden);
        Assert.DoesNotContain(automatic.Id, upgraded.Settings.Receivers.Keys);
    }
    [Fact]
    public void SecondaryAdvertisedEndpointMustAlsoBeUniqueBeforeMigration()
    {
        var settings = new AppSettings(); settings.Options("192.0.2.11:7000").Hidden = true;
        var result = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([
            Service(model: "first"), Service("_raop", "AABBCCDDEE01", host: "192.0.2.11", model: "first"),
            Service(id: "other", host: "192.0.2.11", model: "second")]), settings);
        Assert.Equal(2, result.Receivers.Count);
        Assert.True(result.Settings.Receivers.ContainsKey("192.0.2.11:7000"));
        Assert.All(result.Receivers, r => Assert.False(result.Settings.ReadOptions(r.Id).Hidden));
    }
    [Fact]
    public void OfflineAddressIsParsedAndUnknownIdentityHasNoInventedPort()
    {
        var offline = Receiver.Offline("192.0.2.10:7000"); Assert.Equal("192.0.2.10", offline.Address); Assert.Equal(7000, offline.Port);
        Assert.Empty(Receiver.Offline("AA:BB:CC:DD:EE:01").Address);
        Assert.Equal("[::1]:7000", ReceiverIdentity.Endpoint("::1", 7000));
    }
    [Fact]
    public void SavedAliasesCannotCollapseConflictingLiveReceivers()
    {
        var first = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(), Service("_raop", "other")]), new());
        first.Settings.Options("aabbccddee01").Volume = 40;
        var split = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(model: "one"), Service("_raop", "other", model: "two")]), first.Settings);
        Assert.Equal(2, split.Receivers.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("other", split.Settings.ReceiverAliases["other"]);
    }
    [Fact]
    public void AddressReuseDoesNotInheritAnotherDevicesPreferences()
    {
        var first = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service()]), new());
        first.Settings.Options("aabbccddee01").Hidden = true;
        var next = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service(id: "AA:BB:CC:DD:EE:02")]), first.Settings);
        Assert.Equal("aabbccddee02", Assert.Single(next.Receivers).Id);
        Assert.False(next.Settings.ReadOptions("aabbccddee02").Hidden); Assert.True(next.Settings.ReadOptions("aabbccddee01").Hidden);
    }
    [Fact]
    public async Task MigrationBackupIsOriginalAndCreatedOnlyOnce()
    {
        var folder = Path.Combine(Path.GetTempPath(), "airflash-identity-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "config.json");
        try
        {
            var store = new SettingsStore(path); var original = new AppSettings(); original.Options("AA:BB:CC:DD:EE:01").Hidden = true; store.Save(original);
            var text = File.ReadAllText(path); var migration = ReceiverCatalog.Reconcile(ReceiverAggregator.Build([Service()]), store.Load());
            await store.SaveReceiverIdentityAsync(migration.Settings, migration.NeedsBackup);
            await store.SaveReceiverIdentityAsync(migration.Settings, true);
            Assert.Equal(text, File.ReadAllText(path + ".pre-receiver-identity.bak"));
            var loaded = store.Load(); Assert.Equal(2, loaded.SchemaVersion); Assert.True(loaded.ReadOptions("aabbccddee01").Hidden);
            Assert.Equal(migration.Settings.ReceiverAliases, loaded.ReceiverAliases); Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task FailedBackupLeavesOriginalConfigurationUntouched()
    {
        var folder = Path.Combine(Path.GetTempPath(), "airflash-identity-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "config.json");
        try
        {
            var store = new SettingsStore(path); store.Save(new()); var text = File.ReadAllText(path);
            Directory.CreateDirectory(path + ".pre-receiver-identity.bak");
            await Assert.ThrowsAnyAsync<IOException>(() => store.SaveReceiverIdentityAsync(new() { LastReceiverId = "changed" }, true));
            Assert.Equal(text, File.ReadAllText(path)); Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
