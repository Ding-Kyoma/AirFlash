using System.Text;
using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class MuteRecoveryTests
{
    private static readonly Guid OldOwner = Guid.Parse("3f23b051-0729-4890-bdaf-e19be3422945");
    private const string EndpointId = "synthetic-render-endpoint";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntentPrecedesMutationRepeatedMuteDoesNotRecaptureAndRestoreIsExact(bool previous)
    {
        var backend = new Backend(previous);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        Assert.Equal(MuteRecoveryStatus.NotInitialized, recovery.Snapshot.Status);
        Assert.Equal(0, journal.Reads);
        recovery.Initialize();
        journal.BeforeWrite = bytes =>
        {
            if (State(bytes) == "Intent")
            {
                Assert.Equal(previous, backend.Bit);
                Assert.Empty(backend.Writes);
            }
        };
        recovery.Mute(null);
        var owned = Assert.Single(recovery.Snapshot.Entries);
        Assert.Equal(EndpointId, owned.EndpointId);
        Assert.Equal(previous, owned.PreviousMute);
        Assert.True(owned.Live);
        Assert.Equal(MuteJournalState.Owned, owned.State);
        Assert.Equal(MuteRecoveryStatus.Ready, recovery.Snapshot.Status);
        recovery.Mute(null);
        Assert.Equal([true], backend.Writes);
        recovery.Restore();
        Assert.Equal(previous, backend.Bit);
        Assert.Equal(new[] { true, previous }, backend.Writes);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.Equal("Resolved", State(journal.Bytes!));
        recovery.Restore();
        Assert.Equal(2, backend.Writes.Count);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedIntentNeverMutatesAndBlocksAcquisitionUntilPersistenceRecovers(bool previous)
    {
        var backend = new Backend(previous);
        var journal = new Journal { FailWrites = true };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        Assert.Empty(backend.Writes);
        Assert.Equal(previous, backend.Bit);
        Assert.False(recovery.Snapshot.CanMute);
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        journal.FailWrites = false;
        recovery.Restore();
        Assert.Empty(backend.Writes);
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetterThatMutatesThenThrowsStillHasOwnedMemoryAndRollsBackExactBit(bool previous)
    {
        var backend = new Backend(previous) { ThrowAfterSet = true };
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        Assert.Equal(new[] { true, previous }, backend.Writes);
        Assert.Equal(previous, backend.Bit);
        Assert.True(recovery.Snapshot.CanMute);
        Assert.Equal("Resolved", State(journal.Bytes!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOwnedCommitRollsBackAndPreservesPriorBit(bool previous)
    {
        var backend = new Backend(previous);
        var journal = new Journal { FailState = "Owned" };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        Assert.Equal(new[] { true, previous }, backend.Writes);
        Assert.Equal(previous, backend.Bit);
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void SuccessfulReplacementFollowedByFlushFailureCanSettleWithoutOverwritingLaterChange()
    {
        var backend = new Backend(false);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        journal.FailAfterState = "Resolved";
        Assert.ThrowsAny<Exception>(recovery.Restore);
        Assert.False(backend.Bit);
        Assert.True(Assert.Single(recovery.Snapshot.Entries).KnownRestored);
        backend.Bit = true;
        journal.FailAfterState = null;
        recovery.Restore();
        Assert.True(backend.Bit);
        Assert.Equal(new[] { true, false }, backend.Writes);
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void MultipleEndpointsPreserveIndependentBitsAndDefaultChangeDoesNotRecaptureOwnedMute()
    {
        var backend = new MultipleEndpoints();
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        backend.Default = "synthetic-second";
        recovery.Mute(null);
        Assert.Equal(2, recovery.Snapshot.Entries.Count);
        Assert.False(recovery.Snapshot.Entries[0].PreviousMute);
        Assert.True(recovery.Snapshot.Entries[1].PreviousMute);
        Assert.NotEqual(recovery.Snapshot.Entries[0].Generation, recovery.Snapshot.Entries[1].Generation);
        backend.Unavailable.Add("synthetic-second");
        Assert.ThrowsAny<Exception>(recovery.Restore);
        Assert.False(backend.Bits[EndpointId]);
        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        backend.Unavailable.Clear();
        recovery.Restore();
        Assert.True(backend.Bits["synthetic-second"]);
        Assert.True(recovery.Snapshot.CanMute);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Fact]
    public void RepeatedLiveOwnedMuteRemainsHarmlessWhenRefreshCannotReadEndpoint()
    {
        var backend = new Backend(false);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        backend.FailGet = true;
        recovery.Refresh();
        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        recovery.Mute(null);
        Assert.Equal([true], backend.Writes);
        Assert.ThrowsAny<Exception>(() => recovery.Mute("synthetic-other"));
        backend.FailGet = false;
        recovery.Restore();
        Assert.False(backend.Bit);
    }

    [Fact]
    public void PreInitializePlaybackFailureDoesNotReadMuteOrAccessJournal()
    {
        var backend = new Backend(false);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        Assert.Equal(MuteRecoveryStatus.NotInitialized, recovery.Snapshot.Status);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(0, journal.Reads);
        Assert.Equal(0, journal.Leases);
        Assert.Empty(journal.Written);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordingMuteDoesNotReadMuteOrAccessJournalBeforeInitialization(bool previous)
    {
        var backend = new Backend(previous) { Kind = AudioEndpointKind.Recording, FailGet = true, ThrowAfterSet = true };
        var journal = new Journal { FailReads = true, FailWrites = true };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Mute(null);
        recovery.Mute(EndpointId);
        Assert.Equal(previous, backend.Bit);
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(0, journal.Reads);
        Assert.Equal(0, journal.Leases);
        Assert.Empty(journal.Written);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.Equal(MuteRecoveryStatus.NotInitialized, recovery.Snapshot.Status);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RecordingMutePreservesInitializedOrPendingRecoveryWithoutMuteAccess(bool previous, bool pending)
    {
        var backend = new Backend(previous) { Kind = AudioEndpointKind.Recording, FailGet = true, ThrowAfterSet = true };
        var original = pending ? Saved(previous, "Owned") : null;
        var journal = new Journal { Bytes = original };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var reads = journal.Reads; var leases = journal.Leases;
        recovery.Mute(null);
        recovery.Mute(EndpointId);
        Assert.Equal(previous, backend.Bit);
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(reads, journal.Reads);
        Assert.Equal(leases, journal.Leases);
        Assert.Empty(journal.Written);
        Assert.Equal(original, journal.Bytes);
        Assert.Equal(pending ? MuteRecoveryStatus.Pending : MuteRecoveryStatus.Ready, recovery.Snapshot.Status);
        if (pending) Assert.Null(Assert.Single(recovery.Snapshot.Entries).CurrentMute);
        else Assert.Empty(recovery.Snapshot.Entries);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RecordingEndpointMustHaveValidIdentityBeforeDirectionOrJournalAccess(string identity)
    {
        var backend = new Backend(false) { Kind = AudioEndpointKind.Recording };
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        Assert.Throws<InvalidDataException>(() => recovery.Mute(identity));
        Assert.Equal(0, backend.KindReads);
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(0, journal.Reads);
        Assert.Equal(0, journal.Leases);
        Assert.Empty(journal.Written);
        Assert.True(Assert.Single(backend.Handles).Disposed);
    }

    [Theory]
    [InlineData(AudioEndpointKind.Playback)]
    [InlineData(AudioEndpointKind.Recording)]
    public void ExplicitMuteRejectsDifferentValidIdentityBeforeKindMuteOrJournal(AudioEndpointKind kind)
    {
        var backend = new Backend(false) { Kind = kind, ReturnedId = "synthetic-other-endpoint" };
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        Assert.Throws<InvalidOperationException>(() => recovery.Mute(EndpointId));
        Assert.Equal(0, backend.KindReads);
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(0, journal.Reads);
        Assert.Equal(0, journal.Leases);
        Assert.Empty(journal.Written);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.True(Assert.Single(backend.Handles).Disposed);
    }

    [Theory]
    [InlineData(false, "refresh")]
    [InlineData(true, "refresh")]
    [InlineData(false, "restore")]
    [InlineData(true, "restore")]
    public void WrongReturnedIdentityDuringLiveRecoveryPreservesOwnedBitWithoutEndpointAccess(bool previous, string operation)
    {
        var backend = new Backend(previous);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        var owned = Assert.Single(recovery.Snapshot.Entries);
        var kindReads = backend.KindReads; var gets = backend.Gets;
        backend.ReturnedId = "synthetic-other-endpoint";
        backend.FailKind = backend.FailGet = true;

        if (operation == "refresh") recovery.Refresh();
        else Assert.Throws<AggregateException>(recovery.Restore);

        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        var retained = Assert.Single(recovery.Snapshot.Entries);
        AssertRetainedObligation(owned, retained, journal);
        Assert.Null(retained.CurrentMute);
        Assert.Equal(kindReads, backend.KindReads);
        Assert.Equal(gets, backend.Gets);
        Assert.Equal([true], backend.Writes);
        Assert.True(backend.Bit);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));

        backend.ReturnedId = null; backend.FailKind = backend.FailGet = false;
        recovery.Restore();
        Assert.Equal(previous, backend.Bit);
        Assert.Equal(new[] { true, previous }, backend.Writes);
        Assert.True(recovery.Snapshot.CanMute);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(false, MuteRecoveryChoice.RestoreSaved)]
    [InlineData(true, MuteRecoveryChoice.RestoreSaved)]
    [InlineData(false, MuteRecoveryChoice.KeepCurrent)]
    [InlineData(true, MuteRecoveryChoice.KeepCurrent)]
    public void WrongReturnedIdentityRejectsBothReviewedChoicesWithoutLosingSavedOwnership(bool previous, MuteRecoveryChoice choice)
    {
        var backend = new Backend(!previous);
        var journal = new Journal { Bytes = Saved(previous, "Owned") };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var ticket = recovery.Snapshot;
        var saved = Assert.Single(ticket.Entries);
        var bytes = journal.Bytes!.ToArray(); var reads = journal.Reads;
        var kindReads = backend.KindReads; var gets = backend.Gets;
        backend.ReturnedId = "synthetic-other-endpoint";
        backend.FailKind = backend.FailGet = true;

        Assert.Throws<InvalidOperationException>(() => recovery.Resolve(ticket.Revision,
            saved.Owner, saved.Generation, saved.EndpointId, choice));

        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        var retained = Assert.Single(recovery.Snapshot.Entries);
        AssertRetainedObligation(saved, retained, journal);
        Assert.Null(retained.CurrentMute);
        Assert.Equal(kindReads, backend.KindReads);
        Assert.Equal(gets, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(!previous, backend.Bit);
        Assert.Equal(reads, journal.Reads);
        Assert.Empty(journal.Written);
        Assert.Equal(bytes, journal.Bytes);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));

        backend.ReturnedId = null; backend.FailKind = backend.FailGet = false;
        recovery.Refresh();
        recovery.Resolve(recovery.Snapshot.Revision, saved.Owner, saved.Generation, saved.EndpointId, choice);
        Assert.Equal(choice == MuteRecoveryChoice.RestoreSaved ? previous : !previous, backend.Bit);
        Assert.Equal(choice == MuteRecoveryChoice.RestoreSaved ? new[] { previous } : Array.Empty<bool>(), backend.Writes);
        Assert.Equal(choice == MuteRecoveryChoice.RestoreSaved ? "Resolved" : "Abandoned", State(journal.Bytes!));
        Assert.True(recovery.Snapshot.CanMute);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void UnknownEndpointDirectionCannotReadMuteOrAcquireJournal(int direction)
    {
        var backend = new Backend(false) { Kind = (AudioEndpointKind)direction };
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        Assert.Throws<InvalidOperationException>(() => recovery.Mute(null));
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(0, journal.Reads);
        Assert.Equal(0, journal.Leases);
        Assert.Empty(journal.Written);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.True(Assert.Single(backend.Handles).Disposed);
    }

    [Fact]
    public void EndpointDirectionFailureDoesNotReadMuteOrAcquireJournal()
    {
        var backend = new Backend(false) { FailKind = true };
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        Assert.Throws<IOException>(() => recovery.Mute(null));
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(0, journal.Reads);
        Assert.Equal(0, journal.Leases);
        Assert.Empty(journal.Written);
        Assert.True(Assert.Single(backend.Handles).Disposed);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    public void UncertainRecoveryDirectionPreservesRawObligationWithoutMuteAccess(int direction, bool failDirection)
    {
        var backend = new Backend(false) { Kind = (AudioEndpointKind)direction, FailKind = failDirection };
        var original = Saved(false, "Owned");
        var journal = new Journal { Bytes = original };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Refresh(); recovery.Restore();
        var pending = Assert.Single(recovery.Snapshot.Entries);
        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        Assert.Null(pending.CurrentMute);
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Empty(journal.Written);
        Assert.Equal(original, journal.Bytes);
        backend.Kind = AudioEndpointKind.Playback; backend.FailKind = false; backend.Bit = true;
        recovery.Refresh();
        var returned = Assert.Single(recovery.Snapshot.Entries);
        recovery.Resolve(recovery.Snapshot.Revision, returned.Owner, returned.Generation,
            returned.EndpointId, MuteRecoveryChoice.KeepCurrent);
        Assert.True(backend.Bit);
        Assert.Empty(backend.Writes);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.True(recovery.Snapshot.CanMute);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(false, "Intent")]
    [InlineData(true, "Intent")]
    [InlineData(false, "Owned")]
    [InlineData(true, "Owned")]
    [InlineData(false, "Restoring")]
    [InlineData(true, "Restoring")]
    public void StaleRecordingOwnershipStaysPendingAndCanRecoverWhenPlaybackReturns(bool previous, string state)
    {
        var backend = new Backend(previous) { Kind = AudioEndpointKind.Recording, FailGet = true };
        var original = Saved(previous, state);
        var journal = new Journal { Bytes = original };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Refresh(); recovery.Restore();
        var pending = Assert.Single(recovery.Snapshot.Entries);
        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        Assert.Null(pending.CurrentMute);
        Assert.False(pending.Live);
        Assert.False(pending.KnownRestored);
        Assert.Equal(original, journal.Bytes);
        Assert.Empty(journal.Written);
        Assert.Equal(0, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Throws<InvalidOperationException>(() => recovery.Resolve(recovery.Snapshot.Revision,
            pending.Owner, pending.Generation, pending.EndpointId, MuteRecoveryChoice.RestoreSaved));
        Assert.Equal(original, journal.Bytes);
        backend.Kind = AudioEndpointKind.Playback; backend.FailGet = false; backend.Bit = !previous;
        recovery.Refresh();
        var returned = Assert.Single(recovery.Snapshot.Entries);
        Assert.Equal(!previous, returned.CurrentMute);
        recovery.Resolve(recovery.Snapshot.Revision, returned.Owner, returned.Generation,
            returned.EndpointId, MuteRecoveryChoice.RestoreSaved);
        Assert.Equal(previous, backend.Bit);
        Assert.Equal([previous], backend.Writes);
        Assert.Empty(recovery.Snapshot.Entries);
        Assert.True(recovery.Snapshot.CanMute);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(MuteRecoveryChoice.RestoreSaved)]
    [InlineData(MuteRecoveryChoice.KeepCurrent)]
    public void DirectionChangedAfterReviewCannotRestoreOrAbandonOwnership(MuteRecoveryChoice choice)
    {
        var backend = new Backend(true);
        var original = Saved(false, "Owned");
        var journal = new Journal { Bytes = original };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var shown = Assert.Single(recovery.Snapshot.Entries);
        var reads = journal.Reads; var gets = backend.Gets;
        backend.Kind = AudioEndpointKind.Recording; backend.FailGet = true;
        Assert.Throws<InvalidOperationException>(() => recovery.Resolve(recovery.Snapshot.Revision,
            shown.Owner, shown.Generation, shown.EndpointId, choice));
        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        Assert.Null(Assert.Single(recovery.Snapshot.Entries).CurrentMute);
        Assert.Equal(gets, backend.Gets);
        Assert.Empty(backend.Writes);
        Assert.Equal(reads, journal.Reads);
        Assert.Empty(journal.Written);
        Assert.Equal(original, journal.Bytes);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LivePlaybackDirectionChangeRetainsExactBitForLaterRestoreAndRetry(bool previous)
    {
        var backend = new Backend(previous);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        var owned = Assert.Single(recovery.Snapshot.Entries);
        var gets = backend.Gets;
        backend.Kind = AudioEndpointKind.Recording; backend.FailGet = true;
        recovery.Mute(null);
        Assert.ThrowsAny<Exception>(recovery.Restore);
        recovery.Refresh();
        var retained = Assert.Single(recovery.Snapshot.Entries);
        Assert.Equal(owned.Generation, retained.Generation);
        Assert.Equal(previous, retained.PreviousMute);
        Assert.Null(retained.CurrentMute);
        Assert.Equal(gets, backend.Gets);
        Assert.Equal([true], backend.Writes);
        Assert.False(recovery.Snapshot.CanMute);
        backend.Kind = AudioEndpointKind.Playback; backend.FailGet = false;
        recovery.Restore();
        Assert.Equal(previous, backend.Bit);
        Assert.Equal(new[] { true, previous }, backend.Writes);
        recovery.Mute(null); recovery.Restore();
        Assert.Equal(previous, backend.Bit);
        Assert.Equal(new[] { true, previous, true, previous }, backend.Writes);
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultChangesAcrossRecordingAndPlaybackPreserveOnlyPlaybackOwnership(bool recordingBit)
    {
        var backend = new MultipleEndpoints();
        const string recordingId = "synthetic-recording";
        backend.Bits[recordingId] = recordingBit; backend.Kinds[recordingId] = AudioEndpointKind.Recording;
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        backend.Default = recordingId;
        var writes = journal.Written.Count;
        recovery.Mute(null);
        Assert.Single(recovery.Snapshot.Entries);
        Assert.Equal(writes, journal.Written.Count);
        Assert.Equal(0, backend.Gets.GetValueOrDefault(recordingId));
        Assert.Equal(0, backend.Sets.GetValueOrDefault(recordingId));
        Assert.Equal(recordingBit, backend.Bits[recordingId]);
        backend.Default = "synthetic-second";
        recovery.Mute(null);
        Assert.Equal(2, recovery.Snapshot.Entries.Count);
        recovery.Restore();
        Assert.False(backend.Bits[EndpointId]);
        Assert.True(backend.Bits["synthetic-second"]);
        Assert.Equal(recordingBit, backend.Bits[recordingId]);
        Assert.Equal(0, backend.Sets.GetValueOrDefault(recordingId));
        Assert.All(backend.Handles, handle => Assert.True(handle.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalRestoreContinuesAfterJournalFailureAndKnownRestoreNeverRewritesExternalChange(bool previous)
    {
        var backend = new Backend(previous);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        journal.FailWrites = true;
        Assert.ThrowsAny<Exception>(recovery.Restore);
        Assert.Equal(previous, backend.Bit);
        Assert.True(Assert.Single(recovery.Snapshot.Entries).KnownRestored);
        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        backend.Bit = !previous;
        journal.FailWrites = false;
        recovery.Refresh(); recovery.Restore();
        Assert.Equal(!previous, backend.Bit);
        Assert.Equal(new[] { true, previous }, backend.Writes);
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRestoreKeepsOwnershipAndReturningEndpointCanBeRetried(bool previous)
    {
        var backend = new Backend(previous);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        backend.Unavailable = true;
        Assert.ThrowsAny<Exception>(recovery.Restore);
        Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
        Assert.False(Assert.Single(recovery.Snapshot.Entries).KnownRestored);
        Assert.ThrowsAny<Exception>(() => recovery.Mute("another-synthetic-endpoint"));
        backend.Unavailable = false;
        recovery.Restore();
        Assert.Equal(previous, backend.Bit);
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Theory]
    [InlineData(false, "Intent")]
    [InlineData(false, "Owned")]
    [InlineData(false, "Restoring")]
    [InlineData(true, "Intent")]
    [InlineData(true, "Owned")]
    [InlineData(true, "Restoring")]
    public void RestartedInterruptionIsConservativeAndMatchingPriorBitNeedsNoSetter(bool previous, string state)
    {
        var backend = new Backend(true);
        var journal = new Journal { Bytes = Saved(previous, state) };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        Assert.Empty(backend.Writes);
        if (previous)
        {
            Assert.True(recovery.Snapshot.CanMute);
            Assert.Empty(recovery.Snapshot.Entries);
        }
        else
        {
            Assert.Equal(MuteRecoveryStatus.Pending, recovery.Snapshot.Status);
            var entry = Assert.Single(recovery.Snapshot.Entries);
            Assert.False(entry.Live);
            Assert.False(entry.PreviousMute);
            Assert.True(entry.CurrentMute);
            Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
            recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved);
            Assert.Equal([false], backend.Writes);
            Assert.True(recovery.Snapshot.CanMute);
        }
    }

    [Fact]
    public void DisposeModelsProcessLossByReleasingOnlyLeaseAndRestartDoesNotRecapture()
    {
        var backend = new Backend(false);
        var journal = new Journal();
        var first = new MuteRecoveryCoordinator(backend, journal);
        first.Initialize(); first.Mute(null); first.Dispose();
        Assert.True(backend.Bit);
        Assert.Equal([true], backend.Writes);
        using var restarted = new MuteRecoveryCoordinator(backend, journal);
        restarted.Initialize();
        var entry = Assert.Single(restarted.Snapshot.Entries);
        Assert.False(entry.PreviousMute);
        Assert.False(entry.Live);
        Assert.False(restarted.Snapshot.CanMute);
    }

    [Fact]
    public void ChangedBitAndStaleTicketRejectExplicitRecoveryWithoutMutation()
    {
        var backend = new Backend(true);
        var journal = new Journal { Bytes = Saved(false, "Owned") };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var ticket = recovery.Snapshot;
        var entry = Assert.Single(ticket.Entries);
        backend.Bit = false;
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(ticket.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved));
        Assert.Empty(backend.Writes);
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(ticket.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.KeepCurrent));
        recovery.Refresh();
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void ExplicitRecoveryFailsBeforeMutationOnWriteErrorAndKnownSuccessfulChoiceOnlyRetriesPersistence()
    {
        var backend = new Backend(true);
        var journal = new Journal { Bytes = Saved(false, "Owned") };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var entry = Assert.Single(recovery.Snapshot.Entries);
        journal.FailWrites = true;
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved));
        Assert.Empty(backend.Writes);
        journal.FailWrites = false; journal.FailState = "Resolved";
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved));
        Assert.Equal([false], backend.Writes);
        Assert.True(Assert.Single(recovery.Snapshot.Entries).KnownRestored);
        backend.Bit = true; journal.FailState = null;
        recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved);
        Assert.Equal([false], backend.Writes);
        Assert.True(backend.Bit);
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void FailedNormalSetterRetainsAmbiguousOwnershipUntilExactRetry()
    {
        var backend = new Backend(false);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Mute(null);
        backend.ThrowAfterSet = true;
        Assert.ThrowsAny<Exception>(recovery.Restore);
        var pending = Assert.Single(recovery.Snapshot.Entries);
        Assert.False(pending.KnownRestored);
        Assert.Null(pending.CurrentMute);
        backend.Bit = true;
        recovery.Restore();
        Assert.False(backend.Bit);
        Assert.Equal(new[] { true, false, false }, backend.Writes);
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void EscapedIdentitiesCannotExceedBoundByTakingUnresolvableOwnership()
    {
        var backend = new Backend(false);
        var journal = new Journal();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var admitted = 0;
        for (var index = 0; index < 32; index++)
        {
            var id = index.ToString() + new string('界', 1020);
            try { recovery.Mute(id); admitted++; }
            catch (InvalidDataException) { break; }
        }
        Assert.InRange(admitted, 1, 31);
        Assert.Equal(admitted, backend.Writes.Count);
        Assert.Equal(admitted, recovery.Snapshot.Entries.Count);
        Assert.True(journal.Bytes!.Length <= 65_536);
        recovery.Restore();
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void WrongIdentityOrGenerationCannotResolveAndKeepCurrentIsExplicitAbandonment()
    {
        var backend = new Backend(true);
        var journal = new Journal { Bytes = Saved(false, "Owned") };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var entry = Assert.Single(recovery.Snapshot.Entries);
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(recovery.Snapshot.Revision, Guid.NewGuid(), entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved));
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation + 1, entry.EndpointId, MuteRecoveryChoice.RestoreSaved));
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation, "other-synthetic-id", MuteRecoveryChoice.RestoreSaved));
        recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.KeepCurrent);
        Assert.Empty(backend.Writes);
        Assert.True(backend.Bit);
        Assert.Equal("Abandoned", State(journal.Bytes!));
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void ExternalJournalReplacementAndUnavailableRecoveryNeverOverwriteOrSetEndpoint()
    {
        var backend = new Backend(true) { Unavailable = true };
        var journal = new Journal { Bytes = Saved(false, "Owned") };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        var entry = Assert.Single(recovery.Snapshot.Entries);
        Assert.Null(entry.CurrentMute);
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(recovery.Snapshot.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved));
        backend.Unavailable = false; recovery.Refresh();
        var ticket = recovery.Snapshot;
        var unknown = Encoding.UTF8.GetBytes("future external bytes");
        journal.Bytes = unknown;
        Assert.ThrowsAny<Exception>(() => recovery.Resolve(ticket.Revision, entry.Owner, entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved));
        Assert.Equal(unknown, journal.Bytes);
        Assert.Empty(backend.Writes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"entries\":[]}")]
    [InlineData("{\"version\":1,\"version\":1,\"entries\":[]}")]
    [InlineData("{\"version\":1,\"entries\":[null]}")]
    [InlineData("{\"version\":1,\"entries\":null}")]
    [InlineData("{\"version\":\"1\",\"entries\":[]}")]
    [InlineData("{\"version\":1.5,\"entries\":[]}")]
    [InlineData("not-json")]
    public void UnrecognizedBytesArePreservedAndBlockNewMute(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var backend = new Backend(false);
        var journal = new Journal { Bytes = bytes };
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize(); recovery.Refresh();
        Assert.Equal(MuteRecoveryStatus.Unrecognized, recovery.Snapshot.Status);
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        Assert.Equal(bytes, journal.Bytes);
        Assert.Empty(backend.Writes);
        Assert.Empty(journal.Written);
    }

    [Fact]
    public void DuplicateAndBoundedRecordsAreRejectedWithoutPartialRecovery()
    {
        var one = JsonDocument.Parse(Saved(false, "Owned")).RootElement.GetProperty("entries")[0].GetRawText();
        foreach (var json in new[]
        {
            "{\"version\":1,\"entries\":[" + one + "," + one + "]}",
            "{\"version\":1,\"entries\":[" + string.Join(',', Enumerable.Repeat(one, 33)) + "]}",
            Encoding.UTF8.GetString(Saved(false, "Owned", new string('x', 1025))),
            new string(' ', 65_537)
        })
        {
            var backend = new Backend(false);
            var journal = new Journal { Bytes = Encoding.UTF8.GetBytes(json) };
            using var recovery = new MuteRecoveryCoordinator(backend, journal);
            recovery.Initialize();
            Assert.Equal(MuteRecoveryStatus.Unrecognized, recovery.Snapshot.Status);
            Assert.Empty(backend.Writes);
            Assert.Empty(journal.Written);
        }
    }

    [Fact]
    public void BusyAndUncertainReadCannotBecomeAbsenceAndRefreshCanAcquireReleasedLease()
    {
        var backend = new Backend(false);
        var journal = new Journal();
        using var owner = journal.AcquireLease();
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        Assert.Equal(MuteRecoveryStatus.Busy, recovery.Snapshot.Status);
        Assert.Equal(0, journal.Reads);
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        owner.Dispose();
        journal.FailReads = true;
        recovery.Refresh();
        Assert.Equal(MuteRecoveryStatus.Unreadable, recovery.Snapshot.Status);
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        journal.FailReads = false;
        recovery.Refresh();
        Assert.True(recovery.Snapshot.CanMute);
        Assert.Empty(backend.Writes);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("delete")]
    [InlineData("after-delete")]
    [InlineData("empty-write")]
    public void ArchiveFailuresPreserveBytesOrBackupAndRemainBlockedAcrossRefresh(string failure)
    {
        var bytes = Encoding.UTF8.GetBytes("unrecognized synthetic journal");
        var backend = new Backend(false);
        var journal = new Journal { Bytes = bytes, ArchiveFailure = failure };
        if (failure == "empty-write") journal.FailWrites = true;
        using var recovery = new MuteRecoveryCoordinator(backend, journal);
        recovery.Initialize();
        Assert.ThrowsAny<Exception>(() => recovery.ArchiveUnrecognized(recovery.Snapshot.Revision));
        recovery.Refresh();
        Assert.False(recovery.Snapshot.CanMute);
        Assert.Empty(backend.Writes);
        Assert.True(journal.Bytes?.SequenceEqual(bytes) == true || journal.Archived.Any(copy => copy.SequenceEqual(bytes)));
        Assert.ThrowsAny<Exception>(() => recovery.Mute(null));
        if (failure == "empty-write")
        {
            journal.FailWrites = false;
            recovery.ArchiveUnrecognized(recovery.Snapshot.Revision);
            Assert.True(recovery.Snapshot.CanMute);
        }
    }

    [Fact]
    public void ArchiveRequiresCurrentRevisionAndExactBytesBeforeEnablingNewAcquisition()
    {
        var original = Encoding.UTF8.GetBytes("unknown synthetic bytes");
        var journal = new Journal { Bytes = original };
        using var recovery = new MuteRecoveryCoordinator(new Backend(false), journal);
        recovery.Initialize();
        var revision = recovery.Snapshot.Revision;
        recovery.Refresh();
        Assert.ThrowsAny<Exception>(() => recovery.ArchiveUnrecognized(revision));
        recovery.ArchiveUnrecognized(recovery.Snapshot.Revision);
        Assert.Equal(original, Assert.Single(journal.Archived));
        Assert.True(recovery.Snapshot.CanMute);
    }

    [Fact]
    public void FileLeaseIsExclusiveConstantAndReleasedWithoutReadingOrDeletingJournal()
    {
        using var root = new TemporaryRoot();
        var first = new FileMuteRecoveryJournal(root.Path);
        var second = new FileMuteRecoveryJournal(root.Path);
        var lease = first.AcquireLease();
        try
        {
            Assert.Throws<MuteRecoveryBusyException>(() => second.AcquireLease());
            Assert.Null(first.Read());
            first.Write(Saved(false, "Owned"));
            first.Write(Saved(true, "Intent"));
            Assert.Equal(Saved(true, "Intent"), first.Read());
            Assert.True(File.Exists(System.IO.Path.Combine(root.Path, "mute-recovery.lease")));
        }
        finally { lease.Dispose(); }
        using var reopened = second.AcquireLease();
        Assert.Equal(Saved(true, "Intent"), second.Read());
        Assert.DoesNotContain(Directory.EnumerateFiles(root.Path), file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void FileArchivePreservesExactRawBytesAndRejectsChangedSource()
    {
        using var root = new TemporaryRoot();
        var journal = new FileMuteRecoveryJournal(root.Path);
        using var lease = journal.AcquireLease();
        var bytes = new byte[] { 0xef, 0xbb, 0xbf, 0xff, 0, 27 };
        journal.Write(bytes);
        Assert.ThrowsAny<Exception>(() => journal.Archive([1, 2, 3]));
        Assert.Equal(bytes, journal.Read());
        journal.Archive(bytes);
        Assert.Null(journal.Read());
        var backup = Assert.Single(Directory.EnumerateFiles(root.Path), file => file.Contains(".archive.", StringComparison.Ordinal));
        Assert.Equal(bytes, File.ReadAllBytes(backup));
    }

    [Fact]
    public void FileReadAndWriteStayBoundedAndDirectoryIsNotAbsence()
    {
        using var root = new TemporaryRoot();
        var journal = new FileMuteRecoveryJournal(root.Path);
        using var lease = journal.AcquireLease();
        Assert.ThrowsAny<Exception>(() => journal.Write(new byte[65_537]));
        var path = System.IO.Path.Combine(root.Path, "mute-recovery.json");
        File.WriteAllBytes(path, new byte[65_537]);
        Assert.ThrowsAny<Exception>(() => journal.Read());
        File.Delete(path); Directory.CreateDirectory(path);
        Assert.ThrowsAny<Exception>(() => journal.Read());
    }

    [Fact]
    public void WindowsLockedReplacementAndArchiveDeletionRetainOriginalAndDurableBackup()
    {
        // Windows sharing flags define this failure boundary; the application
        // and its hosted validation use Windows. Other OS unlink rules differ.
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TemporaryRoot();
        var journal = new FileMuteRecoveryJournal(root.Path);
        using var lease = journal.AcquireLease();
        var bytes = Encoding.UTF8.GetBytes("synthetic unrecognized original");
        journal.Write(bytes);
        using (var locked = new FileStream(System.IO.Path.Combine(root.Path, "mute-recovery.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => journal.Write([1, 2, 3]));
            Assert.Equal(bytes, journal.Read());
            Assert.ThrowsAny<Exception>(() => journal.Archive(bytes));
            Assert.Equal(bytes, journal.Read());
            var backup = Assert.Single(Directory.EnumerateFiles(root.Path), file => file.Contains(".archive.", StringComparison.Ordinal));
            Assert.Equal(bytes, File.ReadAllBytes(backup));
        }
        journal.Write([1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, journal.Read());
        Assert.DoesNotContain(Directory.EnumerateFiles(root.Path), file => file.EndsWith(".tmp", StringComparison.Ordinal));
    }

    private static void AssertRetainedObligation(MuteRecoveryEntry expected, MuteRecoveryEntry actual, Journal journal)
    {
        Assert.Equal(expected.Owner, actual.Owner);
        Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.EndpointId, actual.EndpointId);
        Assert.Equal(expected.PreviousMute, actual.PreviousMute);
        Assert.Equal(expected.Live, actual.Live);
        Assert.False(actual.KnownRestored);
        Assert.True(actual.State is MuteJournalState.Intent or MuteJournalState.Owned or MuteJournalState.Restoring);
        using var json = JsonDocument.Parse(journal.Bytes!);
        var persisted = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal(expected.Owner, persisted.GetProperty("owner").GetGuid());
        Assert.Equal(expected.Generation, persisted.GetProperty("generation").GetInt64());
        Assert.Equal(expected.EndpointId, persisted.GetProperty("endpoint_id").GetString());
        Assert.Equal(expected.PreviousMute, persisted.GetProperty("previous_mute").GetBoolean());
        Assert.Contains(persisted.GetProperty("state").GetString(), new[] { "Intent", "Owned", "Restoring" });
    }

    private static byte[] Saved(bool previous, string state, string endpointId = EndpointId)
        => JsonSerializer.SerializeToUtf8Bytes(new { version = 1, entries = new[] { new { owner = OldOwner, generation = 7, endpoint_id = endpointId, previous_mute = previous, state } } });
    private static string State(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("entries")[0].GetProperty("state").GetString()!;
    }

    private sealed class Backend(bool previous) : IMuteEndpointBackend
    {
        public bool Bit = previous, ThrowAfterSet, Unavailable, FailGet, FailKind;
        public AudioEndpointKind Kind = AudioEndpointKind.Playback;
        public string? ReturnedId;
        public int Gets, KindReads;
        public readonly List<bool> Writes = [];
        public readonly List<Endpoint> Handles = [];
        public IMuteEndpoint Open(string? endpointId)
        {
            if (Unavailable) throw new IOException("Synthetic endpoint unavailable");
            var handle = new Endpoint(this, ReturnedId ?? endpointId ?? EndpointId);
            Handles.Add(handle); return handle;
        }
        public sealed class Endpoint(Backend backend, string id) : IMuteEndpoint
        {
            public string Id => id;
            public AudioEndpointKind Kind
            {
                get { backend.KindReads++; return backend.FailKind ? throw new IOException("Synthetic direction read failure") : backend.Kind; }
            }
            public bool Disposed;
            public bool Muted
            {
                get { backend.Gets++; return backend.FailGet ? throw new IOException("Synthetic endpoint read failure") : backend.Bit; }
                set
                {
                    backend.Bit = value; backend.Writes.Add(value);
                    if (backend.ThrowAfterSet) { backend.ThrowAfterSet = false; throw new IOException("Synthetic setter mutated before failure"); }
                }
            }
            public void Dispose() => Disposed = true;
        }
    }

    private sealed class MultipleEndpoints : IMuteEndpointBackend
    {
        public string Default = EndpointId;
        public readonly Dictionary<string, bool> Bits = new(StringComparer.Ordinal) { [EndpointId] = false, ["synthetic-second"] = true };
        public readonly Dictionary<string, AudioEndpointKind> Kinds = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Gets = new(StringComparer.Ordinal), Sets = new(StringComparer.Ordinal);
        public readonly HashSet<string> Unavailable = new(StringComparer.Ordinal);
        public readonly List<Endpoint> Handles = [];
        public IMuteEndpoint Open(string? endpointId)
        {
            var id = endpointId ?? Default;
            if (Unavailable.Contains(id)) throw new IOException("Synthetic endpoint is unavailable");
            var handle = new Endpoint(this, id); Handles.Add(handle); return handle;
        }
        public sealed class Endpoint(MultipleEndpoints backend, string id) : IMuteEndpoint
        {
            public string Id => id;
            public AudioEndpointKind Kind => backend.Kinds.GetValueOrDefault(id, AudioEndpointKind.Playback);
            public bool Disposed;
            public bool Muted
            {
                get { backend.Gets[id] = backend.Gets.GetValueOrDefault(id) + 1; return backend.Bits[id]; }
                set { backend.Sets[id] = backend.Sets.GetValueOrDefault(id) + 1; backend.Bits[id] = value; }
            }
            public void Dispose() => Disposed = true;
        }
    }

    private sealed class Journal : IMuteRecoveryJournal
    {
        public byte[]? Bytes;
        public bool FailWrites, FailReads;
        public string? FailState, FailAfterState, ArchiveFailure;
        public Action<byte[]>? BeforeWrite;
        public int Reads, Leases;
        public readonly List<byte[]> Written = [], Archived = [];
        private bool _leased;
        public IDisposable AcquireLease()
        {
            Leases++;
            if (_leased) throw new MuteRecoveryBusyException("Synthetic lease is already owned");
            _leased = true; return new Lease(() => _leased = false);
        }
        public byte[]? Read()
        {
            Reads++;
            if (FailReads) throw new IOException("Synthetic read failure");
            return Bytes?.ToArray();
        }
        public void Write(byte[] bytes)
        {
            BeforeWrite?.Invoke(bytes);
            if (FailWrites || FailState is not null && State(bytes) == FailState) throw new IOException("Synthetic journal write failure");
            Bytes = bytes.ToArray(); Written.Add(bytes.ToArray());
            if (FailAfterState is not null && State(bytes) == FailAfterState) throw new IOException("Synthetic final flush failure after replacement");
        }
        public void Archive(byte[] expectedBytes)
        {
            if (Bytes is null || !Bytes.SequenceEqual(expectedBytes)) throw new IOException("Synthetic archive source changed");
            if (ArchiveFailure == "backup") throw new IOException("Synthetic backup write failure");
            Archived.Add(expectedBytes.ToArray());
            if (ArchiveFailure == "delete") throw new IOException("Synthetic original deletion failure");
            Bytes = null;
            if (ArchiveFailure == "after-delete") throw new IOException("Synthetic deletion completion failure");
        }
        private sealed class Lease(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airflash-mute-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryRoot() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
