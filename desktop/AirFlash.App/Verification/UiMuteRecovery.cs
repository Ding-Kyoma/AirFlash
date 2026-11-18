using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;

namespace AirFlash.App.Verification;

// Real AudioService queue and application bindings; every endpoint, journal root,
// engine, discovery and settings boundary is synthetic and explicitly isolated.
internal static class UiMuteRecovery
{
    public static async Task RunAsync(List<string> checks, string directory)
    {
        var root = Path.Combine(directory, "mute-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Check(AudioService.RecoveryDirectory == Path.Combine(AppPaths.DataDirectory, "mute-recovery"),
            "diagnostic mute journal follows isolated startup data", checks);
        await ReadOnlyAsync(checks, Path.Combine(root, "read-only"));
        foreach (var previous in new[] { false, true }) await ExactBitAsync(checks, Path.Combine(root, "bit-" + previous), previous);
        foreach (var previous in new[] { false, true }) await RecordingAsync(checks, Path.Combine(root, "recording-" + previous), previous);
        await JournalFailuresAsync(checks, Path.Combine(root, "journal-failures"));
        await MutatingFailureAsync(checks, Path.Combine(root, "setter-failure"));
        await DisposeFailureAsync(checks, Path.Combine(root, "dispose-failure"));
        await StartupAndRecoveryAsync(checks, Path.Combine(root, "startup"));
        await ApplicationCaptureAsync(checks, Path.Combine(root, "application"));
        await PairDuringSaveAsync(checks, Path.Combine(root, "pair-during-save"));
        await QuitDuringStartupAsync(checks, Path.Combine(root, "quit-startup"));
        await ActiveStopAsync(checks, Path.Combine(root, "active-stop"));
    }

    private static async Task ReadOnlyAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory);
        using (var interrupted = new MuteRecoveryCoordinator(backend, journal)) { interrupted.Initialize(); interrupted.Mute(null); }
        backend.Calls.Clear(); backend.Opens = 0; journal.Leases = 0; journal.Reads = 0; journal.Writes = 0;
        var audio = Audio(backend, journal);
        Check(audio.Recovery.Status == MuteRecoveryStatus.NotInitialized, "audio constructor leaves recovery uninitialized", checks);
        await audio.GetEndpointsAsync(); await audio.GetDefaultEndpointIdAsync(); await audio.RestoreAsync(); await audio.DisposeAsync();
        Check(journal.Leases == 0 && journal.Reads == 0 && journal.Writes == 0 && backend.Opens == 0 && backend.Bit,
            "read-only audio catalog and disposal never read stale journal or endpoint mute", checks);
        Check(backend.Calls.All(call => call.Thread != Environment.CurrentManagedThreadId && call.Apartment == ApartmentState.MTA),
            "injected audio catalog executes on the MTA worker", checks);
    }

    private static async Task ExactBitAsync(List<string> checks, string directory, bool previous)
    {
        var backend = new Backend(previous); var journal = new Journal(directory);
        await using (var audio = Audio(backend, journal))
        {
            await audio.InitializeRecoveryAsync(); await audio.MuteAsync(null); await audio.MuteAsync(null);
            var owned = audio.Recovery.Entries.Single();
            Check(owned.EndpointId == Backend.EndpointId && owned.PreviousMute == previous && owned.Live,
                $"MTA journal owns actual default id and original {previous} bit without recapture", checks);
            await audio.RestoreAsync();
            Check(backend.Bit == previous && audio.Recovery.CanMute && audio.Recovery.Entries.Count == 0,
                $"MTA cleanup restores exact {previous} bit and resolves journal", checks);
        }
        using var lease = new FileMuteRecoveryJournal(directory).AcquireLease();
        Check(backend.Calls.All(call => call.Apartment == ApartmentState.MTA), "endpoint access remains on MTA including cleanup", checks);
        checks.Add("audio disposal releases recovery lease after queued cleanup");
    }

    private static async Task RecordingAsync(List<string> checks, string directory, bool previous)
    {
        var backend = new Backend(previous) { Kind = AudioEndpointKind.Recording, RejectMuteAccess = true };
        var journal = new Journal(directory);
        // Deliberately disagree with the catalog: only the opened endpoint's
        // direction authorizes mute access, even with identical friendly names.
        await using (var audio = new AudioService(backend, journal,
            () => [new(Backend.EndpointId, "Synthetic shared name", AudioEndpointKind.Playback),
                new("synthetic-recording-secondary", "Synthetic shared name", AudioEndpointKind.Recording)],
            () => Backend.EndpointId))
        {
            var catalog = await audio.GetEndpointsAsync();
            Check(catalog.Count == 2 && catalog.Select(item => item.Kind).Distinct().Count() == 2
                && catalog.Select(item => item.Id).Distinct().Count() == 2,
                "audio catalog preserves playback and recording identities with shared friendly names", checks);
            await audio.MuteAsync(Backend.EndpointId);
            Check(journal.Leases == 0 && journal.Reads == 0 && journal.Writes == 0 && backend.Getters == 0 && backend.Setters == 0,
                $"opened recording input with prior {previous} bypasses mute access and journal acquisition before initialization", checks);
            await audio.InitializeRecoveryAsync();
            await audio.MuteAsync(null); await audio.MuteAsync(Backend.EndpointId); await audio.RestoreAsync();
            Check(audio.Recovery.CanMute && audio.Recovery.Entries.Count == 0 && journal.Writes == 0
                && backend.Getters == 0 && backend.Setters == 0 && backend.Bit == previous,
                $"recording input with prior {previous} never creates recovery ownership despite playback catalog label", checks);
            backend.Kind = AudioEndpointKind.Playback; backend.RejectMuteAccess = false;
            await audio.MuteAsync(null); await audio.RestoreAsync();
            Check(backend.Bit == previous && backend.Setters == 2 && audio.Recovery.CanMute,
                $"switching to the actual playback endpoint retains exact prior {previous} cleanup", checks);
            var reads = backend.Getters; var writes = journal.Writes;
            backend.Kind = AudioEndpointKind.Recording; backend.RejectMuteAccess = true;
            await audio.MuteAsync(null); await audio.RestoreAsync();
            Check(backend.Getters == reads && backend.Setters == 2 && journal.Writes == writes,
                "switching back to recording neither recaptures nor restores its mute bit", checks);
        }

        var staleBackend = new Backend(previous); var staleJournal = new Journal(Path.Combine(directory, "stale"));
        byte[] raw;
        using (var interrupted = new MuteRecoveryCoordinator(staleBackend, staleJournal))
        { interrupted.Initialize(); interrupted.Mute(null); raw = staleJournal.Read()!; }
        var staleWrites = staleJournal.Writes; var staleReads = staleBackend.Getters;
        staleBackend.Calls.Clear();
        staleBackend.Kind = AudioEndpointKind.Recording; staleBackend.RejectMuteAccess = true;
        await using var recovering = Audio(staleBackend, staleJournal);
        await recovering.InitializeRecoveryAsync();
        var entry = recovering.Recovery.Entries.Single();
        Check(recovering.Recovery.Status == MuteRecoveryStatus.Pending && entry.CurrentMute is null
            && staleBackend.Getters == staleReads && staleBackend.Setters == 1 && staleJournal.Writes == staleWrites
            && raw.SequenceEqual(staleJournal.Read()!),
            "saved recovery for a recording input remains pending with exact journal bytes and no mute access", checks);
        var rejected = false;
        try
        {
            await recovering.ResolveRecoveryAsync(recovering.Recovery.Revision, entry.Owner,
                entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved);
        }
        catch (InvalidOperationException) { rejected = true; }
        await recovering.RestoreAsync();
        Check(rejected && staleBackend.Getters == staleReads && staleBackend.Setters == 1 && raw.SequenceEqual(staleJournal.Read()!),
            "explicit recovery and ordinary cleanup cannot write an unavailable playback mute obligation to a recording input", checks);
        staleBackend.Kind = AudioEndpointKind.Playback; staleBackend.RejectMuteAccess = false;
        await recovering.RefreshRecoveryAsync();
        if (!recovering.Recovery.CanMute)
        {
            entry = recovering.Recovery.Entries.Single();
            await recovering.ResolveRecoveryAsync(recovering.Recovery.Revision, entry.Owner,
                entry.Generation, entry.EndpointId, MuteRecoveryChoice.RestoreSaved);
        }
        Check(recovering.Recovery.CanMute && staleBackend.Bit == previous
            && staleBackend.Calls.All(call => call.Apartment == ApartmentState.MTA),
            $"actual playback return permits controlled exact prior {previous} recovery on the MTA", checks);
    }

    private static async Task JournalFailuresAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory);
        await using var audio = Audio(backend, journal);
        await audio.InitializeRecoveryAsync(); journal.FailWrites = true;
        await ExpectFailure(audio.MuteAsync(null));
        Check(backend.Setters == 0 && !backend.Bit, "failed durable acquisition prevents every mute setter", checks);
        journal.FailWrites = false; await audio.RestoreAsync(); await audio.MuteAsync(null); journal.FailWrites = true;
        await ExpectFailure(audio.RestoreAsync());
        Check(!backend.Bit && audio.Recovery.Entries.Any(entry => entry.KnownRestored),
            "journal failure still attempts exact live restore and retains known-restored status", checks);
        var setters = backend.Setters; backend.ChangeExternally(true);
        await ExpectFailure(audio.RestoreAsync());
        Check(backend.Bit && backend.Setters == setters, "known-restored retry never overwrites a later external mute", checks);
        journal.FailWrites = false; await audio.RestoreAsync();
        Check(audio.Recovery.CanMute && backend.Setters == setters && backend.Bit,
            "resolution persistence retry settles without replaying endpoint restore", checks);
    }

    private static async Task MutatingFailureAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory);
        await using var audio = Audio(backend, journal); await audio.InitializeRecoveryAsync();
        backend.ThrowAfterNextSet = true; await ExpectFailure(audio.MuteAsync(null));
        Check(!backend.Bit && backend.Setters >= 2, "setter mutation followed by exception receives tracked exact-bit rollback", checks);
        await audio.RestoreAsync();
        Check(audio.Recovery.Entries.Count == 0, "failed mute rollback settles its journal ownership", checks);
    }

    private static async Task StartupAndRecoveryAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory);
        // Dispose on the pure coordinator releases only the lease. Omitting Restore
        // models lost process memory, not physical Windows mute persistence.
        using (var interrupted = new MuteRecoveryCoordinator(backend, journal)) { interrupted.Initialize(); interrupted.Mute(null); }
        backend.Calls.Clear(); using var release = new ManualResetEventSlim(); backend.OpenGate = release;
        var audio = Audio(backend, journal); var factory = new Factory();
        var app = new AppViewModel(new Store(), new UiSmoke.MockDiscovery { Items = [] }, new UiSmoke.MockAutostart(), audio, factory, Application.Current.Dispatcher, receiverCapabilities: new Capabilities(), isolatedVerification: true);
        var panel = new ControlPanel(app) { MenuOpen = true };
        MuteRecoveryWindow? recoveryWindow = null;
        SettingsWindow? settings = null;
        try
        {
            app.Start(); panel.ShowPanel();
            await backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var responsive = false;
            await Application.Current.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Background);
            Check(responsive && !app.MuteReady && app.Receivers.All(row => !row.CanPlay),
                "journal inspection blocks muting admission while dispatcher remains responsive", checks);
            var receiver = new Receiver("synthetic-manual", "Synthetic receiver", "192.0.2.20", 7000) { IsManual = true };
            await app.ToggleAsync(receiver);
            await app.PairAsync(receiver, (_, _) => Task.FromResult<string?>("1234"));
            Check(factory.Opened == 0, "pending recovery rejects Play and Pair before any engine command", checks);
            release.Set(); await app.Startup; await Pump();
            Check(app.HasMuteRecovery && !app.MuteReady && !app.Receivers.Single().CanPlay && backend.Bit,
                "stale mismatched recovery remains visible and disables actual Play control without automatic restore", checks);
            settings = new(app) { Owner = panel }; settings.ViewModel.SelectedPage = SettingsViewModel.ReceiversPage;
            settings.Show(); await Pump();
            var editor = settings.ViewModel.Receivers.Single();
            var pair = Descendants(settings).OfType<Button>().Single(button => ReferenceEquals(button.Command, editor.PairCommand));
            var settingsReview = Descendants(settings).OfType<Button>().Single(button => button.Name == "SettingsReviewMuteRecoveryButton");
            Check(pair.IsVisible && !pair.IsEnabled && !editor.PairCommand.CanExecute(null),
                "actual Settings Pair binding rejects pending mute recovery", checks);
            editor.PairCommand.Execute(null); await Pump();
            Check(factory.Opened == 0 && settingsReview.IsVisible && settingsReview.IsEnabled,
                "blocked Settings Pair creates no engine and keeps prominent recovery review accessible", checks);
            settings.Close(); settings = null; await Pump();
            var recoveryMessage = app.MuteRecoveryMessage; app.ShowError(new IOException("synthetic ordinary notice"));
            Check(app.MuteRecoveryMessage == recoveryMessage, "ordinary notice cannot clear unresolved mute recovery", checks);
            var requested = Signal(); app.MuteRecoveryRequested += () =>
            { recoveryWindow = new(app) { Owner = panel }; recoveryWindow.Show(); requested.TrySetResult(); };
            var review = Descendants(panel).OfType<Button>().Single(button => ReferenceEquals(button.Command, app.ReviewMuteRecoveryCommand));
            Check(review.IsVisible && review.IsEnabled, "actual panel exposes an accessible recovery review action", checks);
            Invoke(review); await requested.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Pump();
            var later = Descendants(recoveryWindow!).OfType<Button>().Single(button => button.Name == "RecoveryLaterButton");
            Invoke(later); await Pump();
            Check(app.HasMuteRecovery && !app.MuteReady && backend.Bit, "closing recovery with Later preserves the unresolved saved bit", checks);
            requested = Signal(); Invoke(review); await requested.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Pump();
            var restore = Descendants(recoveryWindow!).OfType<Button>().Single(button => button.Name == "RestoreSavedMuteButton");
            Check(restore.IsVisible && restore.IsEnabled && panel.IsEnabled,
                "modeless recovery exposes actual restore action without disabling panel controls", checks);
            var ready = Ready(app); Invoke(restore); await ready; await Pump();
            Check(app.MuteReady && !app.HasMuteRecovery && !backend.Bit, "explicit app recovery restores saved bit and refreshes admission controls", checks);
            Check(backend.Calls.All(call => call.Apartment == ApartmentState.MTA), "startup and explicit recovery endpoint calls stay off dispatcher", checks);
        }
        finally
        {
            release.Set(); await app.Startup; settings?.Close(); recoveryWindow?.Close(); panel.ShutdownPanel(); await app.DisposeAsync();
        }
        using var lease = new FileMuteRecoveryJournal(directory).AcquireLease();
        checks.Add("clean app disposal joins recovery startup and releases journal lease");
    }

    private static async Task PairDuringSaveAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory); var audio = Audio(backend, journal);
        var store = new UiSmoke.MemoryStore();
        store.Save(new() { AutoConnectOnDiscover = false, MuteWhileStreaming = true,
            ManualReceivers = [new("synthetic-manual", "Synthetic receiver", "192.0.2.20", 7000)] });
        var factory = new Factory();
        var app = new AppViewModel(store, new UiSmoke.MockDiscovery { Items = [] }, new UiSmoke.MockAutostart(),
            audio, factory, Application.Current.Dispatcher, receiverCapabilities: new Capabilities(), isolatedVerification: true);
        var release = Signal(); store.SaveGate = release; store.SaveEntered = Signal();
        Task? persistence = null, pairing = null;
        var pinRequested = false;
        try
        {
            app.Start(); await app.Startup.WaitAsync(TimeSpan.FromSeconds(10));
            Check(app.MuteReady && app.Settings.MuteWhileStreaming && audio.Recovery.CanMute,
                "Pair fixture initializes applied muting settings and ready recovery before persistence", checks);
            app.MasterVolume = 37; persistence = app.FlushVolumeAsync();
            await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!persistence.IsCompleted, "Pair fixture holds a real app settings save at an explicit gate", checks);
            pairing = app.PairAsync(app.AllReceivers.Single(), (_, _) =>
            {
                pinRequested = true;
                throw new InvalidOperationException("This fixture holds Pair before pin_required.");
            });
            try { await factory.PairEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException("Explicit Pair must send its engine command before the unrelated settings save is released.", error);
            }
            await pairing.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!persistence.IsCompleted && factory.Opened == 1 && app.Session.Snapshot.State == PlaybackState.Pairing
                && !pinRequested && !backend.Bit && backend.Setters == 0,
                "ready explicit Pair reaches its fake connection during blocked persistence without opening PIN or muting", checks);
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(app.Session.Snapshot.State == PlaybackState.Idle && !persistence.IsCompleted && !backend.Bit,
                "Pair held before pin_required can be stopped while the unrelated save remains blocked", checks);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (persistence is not null) await persistence.WaitAsync(TimeSpan.FromSeconds(10));
                if (pairing is not null) await pairing.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        Check(store.Saved.MasterVolume == 37 && !backend.Bit,
            "releasing held persistence preserves the pending edit after Pair cleanup", checks);
    }

    private static async Task ApplicationCaptureAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory);
        byte[] saved;
        using (var interrupted = new MuteRecoveryCoordinator(backend, journal))
        { interrupted.Initialize(); interrupted.Mute(null); saved = journal.Read()!; }
        var setters = backend.Setters; var writes = journal.Writes;
        var audio = Audio(backend, journal); var factory = new Factory();
        await using var app = new AppViewModel(new Store(application: true), new UiSmoke.MockDiscovery { Items = [] },
            new UiSmoke.MockAutostart(), audio, factory, Application.Current.Dispatcher, receiverCapabilities: new Capabilities(), isolatedVerification: true);
        app.Start(); await app.Startup;
        Check(app.MuteReady && app.HasMuteRecovery && app.Receivers.Single().CanPlay,
            "application capture remains available while a differing saved output recovery stays visible", checks);
        var streaming = State(app.Session, PlaybackState.Streaming);
        await app.ToggleAsync(app.Receivers.Single().Receiver); await streaming;
        Check(factory.Start is { } start && start.Text("source") == "application"
            && backend.Setters == setters && journal.Writes == writes && saved.SequenceEqual(journal.Read()!),
            "application start preserves exact unresolved journal without changing the endpoint mute bit", checks);
        await app.PairAsync(app.Receivers.Single().Receiver, (_, _) =>
            throw new InvalidOperationException("This fixture holds Pair before pin_required."));
        await factory.PairEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await app.StopAsync();
        Check(app.HasMuteRecovery && app.MuteReady && backend.Bit && backend.Setters == setters
            && journal.Writes == writes && saved.SequenceEqual(journal.Read()!),
            "application Pair and Stop preserve pending output recovery without endpoint mutation", checks);
    }

    private static async Task QuitDuringStartupAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory);
        using (var interrupted = new MuteRecoveryCoordinator(backend, journal)) { interrupted.Initialize(); interrupted.Mute(null); }
        using var release = new ManualResetEventSlim(); backend.OpenGate = release;
        var audio = Audio(backend, journal); var factory = new Factory();
        var app = new AppViewModel(new Store(), new UiSmoke.MockDiscovery { Items = [] }, new UiSmoke.MockAutostart(), audio, factory, Application.Current.Dispatcher, receiverCapabilities: new Capabilities(), isolatedVerification: true);
        Task? closing = null;
        try
        {
            app.Start(); await backend.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            closing = app.DisposeAsync().AsTask();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(app.IsClosing && !closing.IsCompleted && factory.Opened == 0,
                "quit invalidates admission and stays responsive while recovery initialization finishes", checks);
        }
        finally
        {
            release.Set(); await (closing ?? app.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
        var verifier = new FileMuteRecoveryJournal(directory); using var lease = verifier.AcquireLease();
        Check(backend.Bit && verifier.Read() is { Length: > 0 }, "quit during startup preserves stale recovery and releases its lease without restoring it", checks);
    }

    private static async Task ActiveStopAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory); var audio = Audio(backend, journal);
        var app = new AppViewModel(new Store(), new UiSmoke.MockDiscovery { Items = [] }, new UiSmoke.MockAutostart(), audio, new Factory(), Application.Current.Dispatcher, receiverCapabilities: new Capabilities(), isolatedVerification: true);
        var panel = new ControlPanel(app) { MenuOpen = true };
        MuteRecoveryWindow? recoveryWindow = null;
        try
        {
            app.Start(); panel.ShowPanel(); await app.Startup;
            var streaming = State(app.Session, PlaybackState.Streaming); await app.ToggleAsync(app.Receivers.Single().Receiver); await streaming;
            journal.FailWrites = true; await ExpectFailure(audio.RestoreAsync()); await Pump();
            Check(!app.MuteReady && app.HasMuteRecovery && app.Session.Snapshot.IsActive && !backend.Bit,
                "journal failure still restores saved bit and does not hide active transport or Stop controls", checks);
            var setters = backend.Setters; backend.ChangeExternally(true);
            recoveryWindow = new(app) { Owner = panel }; recoveryWindow.Show(); await Pump();
            Check(!Descendants(recoveryWindow).OfType<Button>().Any(button => button.Name is "RestoreSavedMuteButton" or "KeepCurrentMuteButton"),
                "known-restored recovery window offers no misleading endpoint restore or keep choice", checks);
            Check(Descendants(recoveryWindow).OfType<Button>().Any(button => button.Name == "RetryRecoverySaveButton"),
                "known-restored recovery exposes a persistence retry action", checks);
            var row = app.Receivers.Single();
            var stop = Descendants(panel).OfType<Button>().Single(button =>
                ReferenceEquals(button.DataContext, row) && ReferenceEquals(button.Command, app.StopCommand));
            Check(ReferenceEquals(row.PlaybackCommand, app.StopCommand) && stop.IsVisible && stop.IsEnabled && row.Active && panel.IsEnabled,
                "actual visible bound Stop stays accessible through modeless recovery while new playback is blocked", checks);
            var idle = State(app.Session, PlaybackState.Idle); Invoke(stop); await idle; await Pump();
            Check(backend.Bit && backend.Setters == setters && !app.Session.Snapshot.IsActive,
                "Stop after known restoration cleans up attempt without overwriting later external mute", checks);
            var retry = Descendants(recoveryWindow).OfType<Button>().Single(button => button.Name == "RetryRecoverySaveButton");
            journal.FailWrites = false; var ready = Ready(app); Invoke(retry); await ready; await Pump();
            Check(backend.Bit && backend.Setters == setters && app.MuteReady,
                "actual recovery retry saves resolution without reapplying the saved endpoint bit", checks);
        }
        finally { journal.FailWrites = false; recoveryWindow?.Close(); panel.ShutdownPanel(); await app.DisposeAsync(); }
    }

    private static async Task DisposeFailureAsync(List<string> checks, string directory)
    {
        var backend = new Backend(false); var journal = new Journal(directory); var audio = Audio(backend, journal);
        await audio.InitializeRecoveryAsync(); await audio.MuteAsync(null); journal.FailWrites = true;
        await ExpectFailure(audio.DisposeAsync().AsTask());
        Check(!backend.Bit && audio.Recovery.Entries.Any(entry => entry.KnownRestored),
            "audio disposal reports journal failure after attempting exact live restoration", checks);
        var verifier = new FileMuteRecoveryJournal(directory); using var lease = verifier.AcquireLease();
        Check(verifier.Read() is { Length: > 0 },
            "failed disposal releases lease while retaining durable recovery evidence", checks);
    }

    private static AudioService Audio(Backend backend, Journal journal) => new(backend, journal,
        () => { backend.Track(); return [new(Backend.EndpointId, "Synthetic speakers")]; },
        () => { backend.Track(); return Backend.EndpointId; });
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ExpectFailure(Task task)
    {
        try { await task; }
        catch (IOException) { return; }
        catch (AggregateException error) when (error.Flatten().InnerExceptions.All(inner => inner is IOException)) { return; }
        throw new InvalidOperationException("Expected an injected endpoint or journal failure.");
    }
    private static async Task State(SessionController session, PlaybackState state)
    {
        var reached = Signal(); void Changed(SessionSnapshot snapshot) { if (snapshot.State == state) reached.TrySetResult(); }
        session.Changed += Changed;
        try { Changed(session.Snapshot); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { session.Changed -= Changed; }
    }
    private static async Task Pump() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    private static async Task Ready(AppViewModel app)
    {
        var ready = Signal();
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (app.MuteReady) ready.TrySetResult(); }
        app.PropertyChanged += Changed;
        try { Changed(null, new(null)); await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { app.PropertyChanged -= Changed; }
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); ++i)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Check(bool condition, string name, List<string> checks)
    { if (!condition) throw new InvalidOperationException("Mute recovery: " + name); checks.Add(name); }

    private sealed class Backend(bool initial) : IMuteEndpointBackend
    {
        public const string EndpointId = "synthetic-render-endpoint";
        private readonly object _gate = new(); private bool _bit = initial;
        public readonly ConcurrentQueue<(int Thread, ApartmentState Apartment)> Calls = new();
        public readonly TaskCompletionSource OpenEntered = Signal();
        public ManualResetEventSlim? OpenGate;
        public AudioEndpointKind Kind = AudioEndpointKind.Playback;
        public bool ThrowAfterNextSet, RejectMuteAccess; public int Opens, Getters, Setters;
        public bool Bit { get { lock (_gate) return _bit; } }
        public void ChangeExternally(bool value) { lock (_gate) _bit = value; }
        public void Track() => Calls.Enqueue((Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
        public IMuteEndpoint Open(string? id)
        {
            Track(); Interlocked.Increment(ref Opens);
            if (id is not null && id != EndpointId) throw new IOException("Synthetic endpoint is unavailable.");
            if (Interlocked.Exchange(ref OpenGate, null) is { } gate)
            { OpenEntered.TrySetResult(); if (!gate.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Synthetic endpoint gate was not released."); }
            return new Endpoint(this);
        }
        private sealed class Endpoint(Backend backend) : IMuteEndpoint
        {
            public string Id => EndpointId;
            public AudioEndpointKind Kind { get { backend.Track(); return backend.Kind; } }
            public bool Muted
            {
                get
                {
                    backend.Track(); Interlocked.Increment(ref backend.Getters);
                    if (backend.RejectMuteAccess) throw new IOException("Recording mute getter must not be used.");
                    return backend.Bit;
                }
                set
                {
                    backend.Track(); Interlocked.Increment(ref backend.Setters);
                    if (backend.RejectMuteAccess) throw new IOException("Recording mute setter must not be used.");
                    backend.ChangeExternally(value);
                    if (backend.ThrowAfterNextSet) { backend.ThrowAfterNextSet = false; throw new IOException("Synthetic setter changed state before failure."); }
                }
            }
            public void Dispose() => backend.Track();
        }
    }
    private sealed class Journal(string directory) : IMuteRecoveryJournal
    {
        private readonly FileMuteRecoveryJournal _inner = new(directory);
        public bool FailWrites; public int Leases, Reads, Writes;
        public IDisposable AcquireLease() { Interlocked.Increment(ref Leases); return _inner.AcquireLease(); }
        public byte[]? Read() { Interlocked.Increment(ref Reads); return _inner.Read(); }
        public void Write(byte[] bytes) { Interlocked.Increment(ref Writes); if (FailWrites) throw new IOException("Synthetic journal write failure."); _inner.Write(bytes); }
        public void Archive(byte[] bytes) => _inner.Archive(bytes);
    }
    private sealed class Store(bool application = false) : ISettingsStore
    {
        private AppSettings _settings = new() { AutoConnectOnDiscover = false, MuteWhileStreaming = true,
            CaptureMode = application ? "application" : "loopback", CaptureApplicationPath = application ? "synthetic-application.exe" : null,
            ManualReceivers = [new("synthetic-manual", "Synthetic receiver", "192.0.2.20", 7000)] };
        public AppSettings Load() => _settings.Clone();
        public void Save(AppSettings settings) => _settings = settings.Clone();
    }
    private sealed class Capabilities : IReceiverCapabilityCatalog
    {
        public Task<ReceiverCapabilitySet> QueryAsync(Receiver receiver, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            return Task.FromResult(ReceiverCapabilitySet.Unknown);
        }
    }
    private sealed class Factory : IEngineFactory
    {
        public int Opened;
        public JsonElement? Start;
        public TaskCompletionSource PairEntered { get; } = Signal();
        public IEngineConnection Open() { Interlocked.Increment(ref Opened); return new Connection(this); }
        private sealed class Connection(Factory factory) : IEngineConnection
        {
            private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
            public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                if (command == "hello") _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session, @event = "hello", capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "application_capture", "receiver_ownership_guard" } }));
                if (command == "pair") factory.PairEntered.TrySetResult();
                if (command == "start")
                {
                    factory.Start = JsonSerializer.SerializeToElement(parameters);
                    _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session, @event = "streaming" }));
                }
                if (command == "stop") _events.Writer.TryComplete();
                return Task.CompletedTask;
            }
            public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
            { try { return await _events.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; } }
            public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
        }
    }
}
