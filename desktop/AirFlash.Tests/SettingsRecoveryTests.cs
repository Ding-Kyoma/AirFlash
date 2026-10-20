using System.Text;
using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

// Every persistence fixture owns its temporary directory; no user configuration is read.
public sealed class SettingsRecoveryTests
{
    [Fact]
    public void LockedConfigurationRecoversTwiceAndIsPreservedBeforeAnUnlockedSave()
    {
        using var fixture = new Fixture();
        var original = Encoding.UTF8.GetBytes("""{"schema_version":2,"master_volume":37,"private_extension":"original"}""");
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        using (var locked = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AssertDefaults(store.Load());
            Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
            AssertDefaults(store.Load());
            Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
            AssertControlledFailure(() => store.Save(new() { MasterVolume = 11 }));
            Assert.Empty(fixture.Backups());
            fixture.AssertNoTemps();
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        store.Save(new() { MasterVolume = 23 });
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        Assert.Equal(23, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public void ReadableMalformedSourceIsBackedUpByteForByte(string encodingName)
    {
        using var fixture = new Fixture();
        var original = Encode("{ malformed 你好 }", encodingName);
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        AssertDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Empty(fixture.Backups());
        store.Save(new() { MasterVolume = 28 });
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        Assert.Equal(28, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public void ValidEncodedLegacyConfigurationMigratesWithoutLosingOriginalBytes(string encodingName)
    {
        using var fixture = new Fixture();
        var original = Encode("""{"schema_version":1,"master_volume":39,"capture_mode":"virtual","virtual_output_device":"Old endpoint","custom_buffer_ms":4000,"receivers":{"one":{"auto_connect":false,"custom_buffer_ms":5000}},"extra_option":"你好"}""", encodingName);
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        var settings = store.Load();
        Assert.Null(settings.Validate());
        Assert.Equal(4, settings.SchemaVersion);
        Assert.Equal(39, settings.MasterVolume);
        Assert.Equal("endpoint", settings.CaptureMode);
        Assert.Equal("Old endpoint", settings.CaptureEndpoint);
        Assert.Equal(2000, settings.CustomBufferMs);
        Assert.Equal(2000, settings.ReadOptions("one").CustomBufferMs);
        Assert.Null(settings.ReadOptions("one").AutoConnect);
        Assert.Equal("你好", settings.Extra!["extra_option"].GetString());
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        store.Save(settings);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        Assert.Null(new SettingsStore(fixture.Path).Load().Validate());
    }

    [Fact]
    public void MissingConfigurationUsesDefaultsWithoutCreatingARecoveryBackup()
    {
        using var fixture = new Fixture();
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        Assert.Null(store.LoadWarning);
        Assert.False(File.Exists(fixture.Path));
        store.Save(new() { MasterVolume = 19 });
        Assert.Equal(19, new SettingsStore(fixture.Path).Load().MasterVolume);
        Assert.Empty(fixture.Backups());
        fixture.AssertNoTemps();
    }

    [Fact]
    public void DeniedReadRemainsPendingAcrossLoadsAndBlocksSaveUntilItCanPreserveSource()
    {
        using var fixture = new Fixture();
        var original = Encode("""{"schema_version":2,"master_volume":34}""", "utf16-le");
        File.WriteAllBytes(fixture.Path, original);
        var files = new DeniedReads(fixture.Path) { Denied = true };
        var store = new SettingsStore(fixture.Path, files);
        AssertDefaults(store.Load());
        AssertDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        AssertControlledFailure(() => store.Save(new()));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Empty(fixture.Backups());
        files.Denied = false;
        var valid = store.Load();
        Assert.Equal(34, valid.MasterVolume);
        store.Save(new() { MasterVolume = 20 });
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(original));
        Assert.Equal(20, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    [Fact]
    public void UncapturedUnreadableSourceCannotBecomeAnAuthorizedOverwriteWhenItDisappears()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "{ original unreadable }");
        var files = new DeniedReads(fixture.Path) { Denied = true };
        var store = new SettingsStore(fixture.Path, files);
        AssertDefaults(store.Load());
        files.Denied = false;
        File.Delete(fixture.Path);
        AssertDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        AssertControlledFailure(() => store.Save(new()));
        Assert.False(File.Exists(fixture.Path));
        Assert.Empty(fixture.Backups());
        fixture.AssertNoTemps();
    }

    [Fact]
    public void RepeatedLoadsPreserveBothTheFirstAndTheChangedReadableSource()
    {
        using var fixture = new Fixture();
        var first = Encode("{ first malformed }", "utf8-bom");
        var second = Encode("{ second malformed }", "utf16-be");
        var third = Encode("{ third malformed before save }", "utf32-le");
        File.WriteAllBytes(fixture.Path, first);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        File.WriteAllBytes(fixture.Path, second);
        AssertDefaults(store.Load());
        File.WriteAllBytes(fixture.Path, third);
        store.Save(new() { MasterVolume = 41 });
        var backups = fixture.Backups();
        Assert.Equal(3, backups.Length);
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(first));
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(second));
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(third));
        Assert.Equal(41, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    [Fact]
    public void SourceChangedAfterLoadIsAlsoPreservedBeforeReplacement()
    {
        using var fixture = new Fixture();
        var first = Encoding.UTF8.GetBytes("{ malformed before load }");
        var changed = Encoding.UTF8.GetBytes("""{"schema_version":2,"master_volume":44}""");
        File.WriteAllBytes(fixture.Path, first);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        File.WriteAllBytes(fixture.Path, changed);
        store.Save(new() { MasterVolume = 22 });
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(first));
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(changed));
        Assert.Equal(22, new SettingsStore(fixture.Path).Load().MasterVolume);
    }

    [Fact]
    public void ExistingDifferentBackupIsKeptAndCurrentSourceGetsItsOwnBackup()
    {
        using var fixture = new Fixture();
        var older = Encode("{ older preserved content }", "utf16-le");
        var original = Encode("{ current malformed content }", "utf8-bom");
        File.WriteAllBytes(fixture.Path + ".pre-wpf.bak", older);
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        store.Save(new());
        Assert.Equal(older, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        var currentBackup = Assert.Single(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(original));
        Assert.NotEqual(fixture.Path + ".pre-wpf.bak", currentBackup);
        Assert.Null(new SettingsStore(fixture.Path).Load().Validate());
    }

    [Fact]
    public void ExistingIdenticalBackupIsReusedInsteadOfCreatingRedundantCopies()
    {
        using var fixture = new Fixture();
        var original = Encode("{ malformed current content }", "utf32-be");
        File.WriteAllBytes(fixture.Path + ".pre-wpf.bak", original);
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        store.Save(new());
        Assert.Single(fixture.Backups());
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
    }

    [Fact]
    public void BackupWriteFailureLeavesOriginalIntactAndRecoveryCanBeRetried()
    {
        using var fixture = new Fixture();
        var original = Encode("{ malformed and awaiting preservation }", "utf16-le");
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        Directory.CreateDirectory(fixture.Path + ".pre-wpf.bak");
        AssertControlledFailure(() => store.Save(new() { MasterVolume = 9 }));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        fixture.AssertNoTemps();
        Directory.Delete(fixture.Path + ".pre-wpf.bak");
        store.Save(new() { MasterVolume = 27 });
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        Assert.Equal(27, new SettingsStore(fixture.Path).Load().MasterVolume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AReadBeforeFailedBackupIsRetainedEvenIfSourceChangesBeforeRetry(bool earlierSourceCaptured)
    {
        using var fixture = new Fixture();
        var earlier = Encode("{ earlier source already captured }", "utf32-be");
        var atFailure = Encode("""{"schema_version":2,"master_volume":31}""", "utf16-le");
        var atRetry = Encode("""{"schema_version":2,"master_volume":32}""", "utf8-bom");
        var files = new DeniedReads(fixture.Path);
        var store = new SettingsStore(fixture.Path, files);
        if (earlierSourceCaptured)
        {
            File.WriteAllBytes(fixture.Path, earlier);
            AssertDefaults(store.Load());
            File.WriteAllBytes(fixture.Path, atFailure);
        }
        else
        {
            File.WriteAllBytes(fixture.Path, atFailure);
            files.Denied = true;
            AssertDefaults(store.Load());
            files.Denied = false;
        }
        Directory.CreateDirectory(fixture.Path + ".pre-wpf.bak");
        AssertControlledFailure(() => store.Save(new() { MasterVolume = 10 }));
        Assert.Equal(atFailure, File.ReadAllBytes(fixture.Path));
        Assert.Empty(fixture.Backups());
        fixture.AssertNoTemps();
        File.WriteAllBytes(fixture.Path, atRetry);
        Directory.Delete(fixture.Path + ".pre-wpf.bak");
        store.Save(new() { MasterVolume = 20 });
        var backups = fixture.Backups();
        if (earlierSourceCaptured)
            Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(earlier));
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(atFailure));
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(atRetry));
        Assert.Equal(20, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    [Fact]
    public void FailedReplacementRetainsPreservationStateAndPreservesNewBytesOnRetry()
    {
        using var fixture = new Fixture();
        var original = Encode("{ failed replacement original }", "utf8-bom");
        var changed = Encode("{ external change before retry }", "utf16-be");
        File.WriteAllBytes(fixture.Path, original);
        var files = new FailingReplacement { Fail = true };
        var store = new SettingsStore(fixture.Path, files);
        AssertDefaults(store.Load());
        Assert.Throws<IOException>(() => store.Save(new() { MasterVolume = 12 }));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak"));
        fixture.AssertNoTemps();
        File.WriteAllBytes(fixture.Path, changed);
        files.Fail = false;
        store.Save(new() { MasterVolume = 32 });
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(original));
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(changed));
        Assert.Equal(32, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
        var backups = fixture.Backups();
        store.Save(new() { MasterVolume = 33 });
        Assert.Equal(backups.Order(), fixture.Backups().Order());
    }

    [Fact]
    public void ACorruptedBackupAfterFailedReplacementIsNotReportedAsVerifiedOnRetry()
    {
        using var fixture = new Fixture();
        var original = Encode("{ source preserved before replacement failure }", "utf16-le");
        var corrupted = Encoding.UTF8.GetBytes("{ externally corrupted older backup }");
        File.WriteAllBytes(fixture.Path, original);
        var files = new FailingReplacement { Fail = true };
        var store = new SettingsStore(fixture.Path, files);
        AssertDefaults(store.Load());
        Assert.Throws<IOException>(() => store.Save(new()));
        var oldBackup = fixture.Path + ".pre-wpf.bak";
        Assert.Equal(original, File.ReadAllBytes(oldBackup));
        File.WriteAllBytes(oldBackup, corrupted);
        files.Fail = false;
        store.Save(new() { MasterVolume = 24 });
        Assert.Equal(corrupted, File.ReadAllBytes(oldBackup));
        var verifiedBackup = Assert.Single(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(original));
        Assert.NotEqual(oldBackup, verifiedBackup);
        Assert.NotNull(store.LoadWarning);
        Assert.Contains(verifiedBackup, store.LoadWarning, StringComparison.Ordinal);
        Assert.DoesNotContain(oldBackup, store.LoadWarning, StringComparison.Ordinal);
        Assert.Equal(24, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("schema_version")]
    [InlineData("Schema_Version")]
    [InlineData("SCHEMA_VERSION")]
    public void FutureSchemaIsFatalAndCannotBeOverwrittenByCallingSave(string propertyName)
    {
        using var fixture = new Fixture();
        var original = Encoding.UTF8.GetBytes($$"""{"{{propertyName}}":99,"master_volume":35}""");
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        Assert.Throws<NotSupportedException>(() => store.Load());
        Assert.Throws<NotSupportedException>(() => store.Save(new()));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Empty(fixture.Backups());
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("5", false)]
    [InlineData("5", true)]
    [InlineData("2147483648", false)]
    [InlineData("2147483648", true)]
    [InlineData("18446744073709551615", false)]
    [InlineData("18446744073709551615", true)]
    [InlineData("999999999999999999999999999999999999999999999999999999999999999999999999", false)]
    [InlineData("999999999999999999999999999999999999999999999999999999999999999999999999", true)]
    public void EveryPositiveFutureIntegerIsFatalEvenOutsideInt32AndBesideASupportedDuplicate(string version, bool duplicate)
    {
        using var fixture = new Fixture();
        var original = Encoding.UTF8.GetBytes(duplicate
            ? "{\"SCHEMA_VERSION\":" + version + ",\"schema_version\":2,\"master_volume\":37}"
            : "{\"schema_version\":" + version + ",\"master_volume\":37}");
        File.WriteAllBytes(fixture.Path, original); var store = new SettingsStore(fixture.Path);
        var load = Record.Exception(() => store.Load());
        var save = Record.Exception(() => store.Save(new() { MasterVolume = 13 }));
        var unchanged = original.SequenceEqual(File.ReadAllBytes(fixture.Path));
        Assert.True(load is NotSupportedException && save is NotSupportedException && unchanged && fixture.Backups().Length == 0,
            $"future integer {version}, duplicate {duplicate}: load={load?.GetType().Name ?? "accepted"}; save={save?.GetType().Name ?? "accepted"}; original unchanged={unchanged}; recovery backups={fixture.Backups().Length}");
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("2147483648", false)]
    [InlineData("2147483648", true)]
    [InlineData("18446744073709551615", false)]
    [InlineData("18446744073709551615", true)]
    public void ACurrentOverflowingFutureSchemaBlocksRecoverySaveBeforeAnyBackup(string version, bool originalCaptured)
    {
        using var fixture = new Fixture(); var files = new DeniedReads(fixture.Path);
        var store = new SettingsStore(fixture.Path, files);
        File.WriteAllText(fixture.Path, "{ earlier malformed source }");
        files.Denied = !originalCaptured; AssertDefaults(store.Load()); files.Denied = false;
        var future = Encoding.UTF8.GetBytes("{\"schema_version\":" + version + "}"); File.WriteAllBytes(fixture.Path, future);
        Assert.Throws<NotSupportedException>(() => store.Save(new()));
        Assert.Throws<NotSupportedException>(() => store.Save(new()));
        Assert.Equal(future, File.ReadAllBytes(fixture.Path)); Assert.Empty(fixture.Backups()); fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("2147483648", false)]
    [InlineData("2147483648", true)]
    [InlineData("18446744073709551615", false)]
    [InlineData("18446744073709551615", true)]
    public async Task IdentitySaveRejectsAnOverflowingFutureSchemaBeforeItsBackupOrReplacement(string version, bool identityBackup)
    {
        using var fixture = new Fixture(); File.WriteAllText(fixture.Path, "{ malformed before identity save }");
        var store = new SettingsStore(fixture.Path); AssertDefaults(store.Load());
        var future = Encoding.UTF8.GetBytes("{\"SCHEMA_VERSION\":" + version + ",\"schema_version\":2}"); File.WriteAllBytes(fixture.Path, future);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveReceiverIdentityAsync(new(), identityBackup));
        Assert.Throws<NotSupportedException>(() => store.Save(new()));
        Assert.Equal(future, File.ReadAllBytes(fixture.Path)); Assert.Empty(fixture.Backups());
        Assert.False(File.Exists(fixture.Path + ".pre-receiver-identity.bak")); fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    [InlineData("2e0")]
    [InlineData("3e0")]
    [InlineData("-2147483649")]
    [InlineData("\"18446744073709551615\"")]
    public void NonIntegerSchemaSpellingsKeepTheirExistingRecoverableValidation(string version)
    {
        using var fixture = new Fixture(); var original = Encoding.UTF8.GetBytes("{\"schema_version\":" + version + "}");
        File.WriteAllBytes(fixture.Path, original); var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load()); Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning)); store.Save(new());
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-wpf.bak")); fixture.AssertNoTemps();
    }

    [Fact]
    public void AValidLoadCanClearAnEarlierFutureSchemaBlock()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, """{"schema_version":99}""");
        var store = new SettingsStore(fixture.Path);
        Assert.Throws<NotSupportedException>(() => store.Load());
        File.WriteAllText(fixture.Path, """{"schema_version":4,"master_volume":31}""");
        var settings = store.Load();
        Assert.Equal(31, settings.MasterVolume);
        settings.MasterVolume = 21;
        store.Save(settings);
        Assert.Equal(21, new SettingsStore(fixture.Path).Load().MasterVolume);
        Assert.Empty(fixture.Backups());
    }

    [Fact]
    public void ACurrentFutureSchemaBlocksSaveEvenWhenRecoveryCapturedAnOlderSource()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "{ recoverable malformed source }");
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        var future = Encoding.UTF8.GetBytes("""{"SCHEMA_VERSION":5}""");
        File.WriteAllBytes(fixture.Path, future);
        Assert.Throws<NotSupportedException>(() => store.Save(new()));
        Assert.Equal(future, File.ReadAllBytes(fixture.Path));
        fixture.AssertNoTemps();
    }

    [Fact]
    public void AmbiguousSupportedSchemaKeysRecoverWithoutLosingTheSource()
    {
        using var fixture = new Fixture();
        var original = Encoding.UTF8.GetBytes("""{"Schema_Version":1,"schema_version":2,"master_volume":45}""");
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        store.Save(new());
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(original));
    }

    [Fact]
    public async Task IdentitySavePreservesMalformedRecoverySourceBeforeReplacement()
    {
        using var fixture = new Fixture();
        var original = Encode("{ malformed before identity save }", "utf16-be");
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        var settings = store.Load();
        settings.Options("aabbccddee01").Hidden = true;
        await store.SaveReceiverIdentityAsync(settings, true);
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(original));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path + ".pre-receiver-identity.bak"));
        Assert.True(new SettingsStore(fixture.Path).Load().ReadOptions("aabbccddee01").Hidden);
        fixture.AssertNoTemps();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityBackupFailureRetainsEverySourceReadBeforeALaterRetry(bool earlierSourceCaptured)
    {
        using var fixture = new Fixture();
        var earlier = Encode("{ previously captured before identity save }", "utf32-be");
        var atFailure = Encode("""{"schema_version":2,"master_volume":31}""", "utf16-le");
        var atRetry = Encode("""{"schema_version":2,"master_volume":32}""", "utf8-bom");
        var files = new FailingIdentityBackup(fixture.Path) { Fail = true };
        var store = new SettingsStore(fixture.Path, files);
        if (earlierSourceCaptured)
        {
            File.WriteAllBytes(fixture.Path, earlier);
            AssertDefaults(store.Load());
            File.WriteAllBytes(fixture.Path, atFailure);
        }
        else
        {
            File.WriteAllBytes(fixture.Path, atFailure);
            files.DenyConfigurationRead = true;
            AssertDefaults(store.Load());
            files.DenyConfigurationRead = false;
        }
        await Assert.ThrowsAsync<IOException>(() => store.SaveReceiverIdentityAsync(new() { MasterVolume = 10 }, true));
        Assert.Equal(atFailure, File.ReadAllBytes(fixture.Path));
        Assert.False(File.Exists(fixture.Path + ".pre-receiver-identity.bak"));
        Assert.Empty(fixture.Backups());
        fixture.AssertNoTemps();
        File.WriteAllBytes(fixture.Path, atRetry);
        files.Fail = false;
        await store.SaveReceiverIdentityAsync(new() { MasterVolume = 20 }, true);
        var backups = fixture.Backups();
        Assert.Equal(earlierSourceCaptured ? 3 : 2, backups.Length);
        if (earlierSourceCaptured)
            Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(earlier));
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(atFailure));
        Assert.Contains(backups, path => File.ReadAllBytes(path).SequenceEqual(atRetry));
        Assert.Equal(atRetry, File.ReadAllBytes(fixture.Path + ".pre-receiver-identity.bak"));
        Assert.Equal(20, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void InvalidLoadedSettingsRecoverBeforeTheyReachRuntime(string json)
    {
        using var fixture = new Fixture();
        var original = Encode(json, "utf8-bom");
        File.WriteAllBytes(fixture.Path, original);
        var store = new SettingsStore(fixture.Path);
        AssertDefaults(store.Load());
        Assert.False(string.IsNullOrWhiteSpace(store.LoadWarning));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        store.Save(new() { MasterVolume = 29 });
        Assert.Contains(fixture.Backups(), path => File.ReadAllBytes(path).SequenceEqual(original));
        Assert.Equal(29, new SettingsStore(fixture.Path).Load().MasterVolume);
        fixture.AssertNoTemps();
    }

    public static TheoryData<string> InvalidConfigurations => new()
    {
        "[]", "null", "42",
        """{"schema_version":2,"manual_receivers":[null]}""",
        """{"schema_version":2,"manual_receivers":[{"id":null,"name":"Manual","host":"speaker.local","port":7000}]}""",
        """{"schema_version":2,"manual_receivers":[{"id":" ","name":"Manual","host":"speaker.local","port":7000}]}""",
        """{"schema_version":2,"manual_receivers":[{"id":"manual","name":"Manual","host":null,"port":7000}]}""",
        """{"schema_version":2,"manual_receivers":[{"id":"manual","name":"Manual","host":" ","port":7000}]}""",
        """{"schema_version":2,"manual_receivers":[{"id":"manual","host":"speaker.local"}]}""",
        """{"schema_version":2,"manual_receivers":[{"id":"manual","host":"speaker.local","port":65536}]}""",
        """{"schema_version":2,"manual_receivers":[{"id":"same","host":"first.local","port":7000},{"id":"same","host":"second.local","port":7001}]}""",
        """{"schema_version":2,"manual_receivers":{}}""",
        """{"schema_version":2,"receivers":[]}""",
        """{"schema_version":2,"receiver_aliases":[]}""",
        """{"schema_version":2,"receivers":{"one":{"sample_rate":"invalid"}}}""",
        """{"schema_version":2,"receivers":{"one":{"sample_rate":"96000"}}}""",
        """{"schema_version":2,"receivers":{"one":{"sample_rate":44100}}}""",
        """{"schema_version":2,"software_groups":[{"id":"software:one","name":"Stereo","left_id":"left","right_id":"right","sample_rate":"invalid"}]}""",
        """{"schema_version":2,"software_groups":[{"id":"software:one","name":"Stereo","left_id":"left","right_id":"right","sample_rate":null}]}""",
        """{"schema_version":2,"ui_language":null}""",
        """{"schema_version":2,"theme":"invalid"}""",
        """{"schema_version":2,"discovery_interface_id":"invalid"}""",
        """{"schema_version":2,"master_volume":101}""",
        """{"schema_version":2,"max_reconnect_attempts":0}""",
        """{"schema_version":2,"latency_mode":null}""",
        """{"schema_version":2,"custom_buffer_ms":2001}""",
        """{"schema_version":2,"standby_silence_seconds":4}""",
        """{"schema_version":2,"capture_mode":"invalid"}""",
        """{"schema_version":2,"capture_mode":"endpoint","capture_endpoint":null}""",
        """{"schema_version":2,"equalizer":{"band_gains_db":null}}""",
        """{"schema_version":2,"equalizer":{"band_gains_db":[0]}}""",
        """{"schema_version":2,"equalizer":{"preamp_db":13}}""",
        """{"schema_version":2,"receivers":{"one":{"volume":101}}}""",
        """{"schema_version":2,"receivers":{"one":{"latency_mode":"invalid"}}}""",
        """{"schema_version":2,"receivers":{"one":{"custom_buffer_ms":2001}}}""",
        """{"schema_version":2,"receivers":{"one":{"standby_seconds":4}}}"""
    };

    [Theory]
    [InlineData("manual-null")]
    [InlineData("manual-entry-null")]
    [InlineData("receivers-null")]
    [InlineData("receiver-entry-null")]
    [InlineData("aliases-null")]
    [InlineData("rate-invalid")]
    public void ValidationReturnsADiagnosticForInvalidNullableShapes(string shape)
    {
        var settings = new AppSettings();
        switch (shape)
        {
            case "manual-null": settings.ManualReceivers = null!; break;
            case "manual-entry-null": settings.ManualReceivers.Add(null!); break;
            case "receivers-null": settings.Receivers = null!; break;
            case "receiver-entry-null": settings.Receivers["one"] = null!; break;
            case "aliases-null": settings.ReceiverAliases = null!; break;
            case "rate-invalid": settings.Options("one").SampleRate = "96000"; break;
            default: throw new ArgumentException(shape);
        }
        Assert.False(string.IsNullOrWhiteSpace(settings.Validate()));
    }

    [Fact]
    public void NormalizedNullCollectionsAndReceiverOptionsRemainCompatible()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, """{"schema_version":4,"manual_receivers":null,"receiver_aliases":null,"receivers":{"removed":null,"kept":{"auto_connect":false}}}""");
        var store = new SettingsStore(fixture.Path);
        var settings = store.Load();
        Assert.Null(settings.Validate());
        Assert.Null(store.LoadWarning);
        Assert.Empty(settings.ManualReceivers);
        Assert.Empty(settings.ReceiverAliases);
        Assert.False(settings.Receivers.ContainsKey("removed"));
        Assert.False(settings.ReadOptions("kept").AutoConnect);
        store.Save(settings);
        Assert.False(new SettingsStore(fixture.Path).Load().ReadOptions("kept").AutoConnect);
        Assert.Empty(fixture.Backups());
        File.WriteAllText(fixture.Path, """{"schema_version":4,"receivers":null}""");
        Assert.Empty(store.Load().Receivers);
        Assert.Null(store.LoadWarning);
    }

    [Fact]
    public void ValidManualRowsExtensionsNullableOverridesAndOfflineAdapterSurviveSave()
    {
        using var fixture = new Fixture();
        const string adapter = "01db7c77-1016-49cb-8f5c-f5c194425d15";
        const string json = """{"schema_version":4,"discovery_interface_id":"01db7c77-1016-49cb-8f5c-f5c194425d15","manual_receivers":[{"id":"manual","name":null,"host":"speaker.local","port":7000},{"id":"MANUAL","name":"","host":"192.0.2.10","port":7001}],"receivers":{"manual":{"auto_connect":false,"volume":null,"latency_mode":null,"custom_buffer_ms":null,"standby_seconds":null}},"future_root":{"enabled":true},"equalizer":{"future_equalizer":[1,2,3]},"receiver_aliases":{"AA:BB:CC:DD:EE:01":"AA-BB-CC-DD-EE-02","endpoint:192.0.2.10:7000":"aabbccddee01"}}""";
        File.WriteAllText(fixture.Path, json);
        var store = new SettingsStore(fixture.Path);
        var settings = store.Load();
        Assert.Null(settings.Validate());
        Assert.Null(store.LoadWarning);
        Assert.Equal(adapter, settings.DiscoveryInterfaceId);
        Assert.Equal(2, settings.ManualReceivers.Count);
        Assert.Null(settings.ManualReceivers[0].Name);
        Assert.Equal("", settings.ManualReceivers[1].Name);
        Assert.False(settings.ReadOptions("manual").AutoConnect);
        Assert.Null(settings.ReadOptions("manual").CustomBufferMs);
        Assert.True(settings.Extra!["future_root"].GetProperty("enabled").GetBoolean());
        Assert.Equal(3, settings.Equalizer.Extra!["future_equalizer"].GetArrayLength());
        Assert.Equal("aabbccddee02", Assert.Single(settings.ReceiverAliases).Value);
        store.Save(settings);
        var roundTrip = new SettingsStore(fixture.Path).Load();
        Assert.True(SettingsMerge.Equal(settings, roundTrip));
        Assert.Empty(fixture.Backups());
        fixture.AssertNoTemps();
    }

    private static void AssertDefaults(AppSettings settings)
    {
        Assert.Null(settings.Validate());
        Assert.Equal(4, settings.SchemaVersion);
        Assert.Equal(100, settings.MasterVolume);
        Assert.False(settings.AutoConnectOnDiscover);
        Assert.False(settings.ForceReconnect);
        Assert.False(settings.StartAtLogin);
        Assert.Empty(settings.ManualReceivers);
        Assert.Empty(settings.Receivers);
    }

    private static void AssertControlledFailure(Action action)
    {
        var error = Assert.ThrowsAny<Exception>(action);
        Assert.True(error is IOException or UnauthorizedAccessException or InvalidOperationException, error.ToString());
    }

    private static byte[] Encode(string text, string name)
    {
        Encoding encoding = name switch
        {
            "utf8" => new UTF8Encoding(false),
            "utf8-bom" => new UTF8Encoding(true),
            "utf16-le" => new UnicodeEncoding(false, true),
            "utf16-be" => new UnicodeEncoding(true, true),
            "utf32-le" => new UTF32Encoding(false, true),
            "utf32-be" => new UTF32Encoding(true, true),
            _ => throw new ArgumentException(name)
        };
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airflash-settings-recovery-tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(DirectoryPath, "config.json");
        public Fixture() => Directory.CreateDirectory(DirectoryPath);
        public string[] Backups() => Directory.GetFiles(DirectoryPath, "config.json.pre-wpf*.bak");
        public void AssertNoTemps() => Assert.Empty(Directory.GetFiles(DirectoryPath, "*.tmp"));
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    private sealed class DeniedReads(string deniedPath) : SettingsFileSystem
    {
        public bool Denied { get; set; }
        internal override byte[] ReadAllBytes(string path)
        {
            if (Denied && path == deniedPath) throw new UnauthorizedAccessException("Synthetic read denial.");
            return base.ReadAllBytes(path);
        }
    }

    private sealed class FailingReplacement : SettingsFileSystem
    {
        public bool Fail { get; set; }
        internal override void Replace(string source, string destination)
        {
            if (Fail) throw new IOException("Synthetic replacement failure.");
            base.Replace(source, destination);
        }
    }

    private sealed class FailingIdentityBackup(string configurationPath) : SettingsFileSystem
    {
        public bool Fail { get; set; }
        public bool DenyConfigurationRead { get; set; }
        internal override byte[] ReadAllBytes(string path)
        {
            if (DenyConfigurationRead && path == configurationPath) throw new UnauthorizedAccessException("Synthetic startup read denial.");
            return base.ReadAllBytes(path);
        }
        internal override void WriteNewBytes(string path, byte[] bytes)
        {
            if (Fail && path == configurationPath + ".pre-receiver-identity.bak") throw new IOException("Synthetic identity backup write failure after source capture.");
            base.WriteNewBytes(path, bytes);
        }
    }
}
