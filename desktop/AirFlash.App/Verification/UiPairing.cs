using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AirFlash.App.Ui;
using AirFlash.Core;

namespace AirFlash.App.Verification;

// Actual modal input and command-path verification. Platform audio, discovery,
// settings and receiver services are never constructed by this fixture.
internal static class UiPairing
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);
    private static Action<PinDialog>? _loaded;
    private enum InputMode { Assigned, Typed, Clipboard }
    private enum Completion { Submit, Cancel, Stop, Timeout, IpcError, EndOfStream, Stopped }

    static UiPairing()
    {
        // The observer exists only after this opt-in fixture is initialized. It
        // observes the real dialog rather than changing its production API.
        EventManager.RegisterClassHandler(typeof(PinDialog), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => _loaded?.Invoke((PinDialog)sender)));
    }

    public static async Task RunAsync(List<string> checks, string directory, bool useClipboard = false)
    {
        _ = directory;
        if (useClipboard) RequireDisposableForkRunner();
        var clipboard = useClipboard ? SnapshotClipboard() : null;
        try
        {
            await PreCancelledAsync(checks);
            await CaseAsync("12-34", "1234", checks, input: InputMode.Typed);
            foreach (var (text, pin) in new (string, string?)[]
            {
                ("12-34", "1234"), ("1234", "1234"), ("12345678", "12345678"), ("12-34-56-78", "12345678"), ("--------1234----", "1234"),
                ("", null), ("123", null), ("123456789", null), ("12a34", null),
                ("12 34", null), (" 1234", null), ("1234\n", null), ("１２３４", null), ("١٢٣٤", null), ("12−34", null), ("-------------1234", null)
            }) await CaseAsync(text, pin, checks);
            foreach (var completion in new[] { Completion.Cancel, Completion.Stop, Completion.Timeout })
                await CaseAsync("1234", "1234", checks, completion: completion);

            foreach (var completion in new[] { Completion.IpcError, Completion.EndOfStream, Completion.Stopped })
                await CaseAsync("1234", "1234", checks, completion: completion);
            await RunPasswordAsync(checks, directory);

            if (useClipboard)
                foreach (var (text, inserted, pin) in new (string, string, string?)[]
                {
                    ("12-34", "12-34", "1234"), ("1234", "1234", "1234"), ("12345678", "12345678", "12345678"),
                    ("12-34-56-78", "12-34-56-78", "12345678"), ("--------1234----", "--------1234----", "1234"),
                    ("-------------1234", "-------------123", null), ("123456789", "123456789", null),
                    ("123", "123", null), ("123a", "123a", null), ("12 34", "12 34", null), (" 1234", " 1234", null),
                    ("１２３４", "１２３４", null), ("١٢٣٤", "١٢٣٤", null), ("12−34", "12−34", null),
                    ("12\n34", "12", null), ("1234\n", "1234", "1234")
                }) await CaseAsync(text, pin, checks, input: InputMode.Clipboard, inserted: inserted);
        }
        finally
        {
            _loaded = null;
            if (useClipboard)
            {
                if (clipboard is null) System.Windows.Clipboard.Clear();
                else System.Windows.Clipboard.SetDataObject(clipboard, copy: true);
            }
        }
    }

    public static async Task RunPasswordAsync(List<string> checks, string directory)
    {
        _ = directory;
        try
        {
            // This value is within the ASCII length of a valid input, but its
            // 258 UTF-8 bytes exceed the native limit. Keep this decisive case
            // first so a red run cannot be manufactured by later cancellation.
            await CaseAsync(new string('界', 86), null, checks, password: true);
            // Bypass only the fake dialog's disabled button to exercise the
            // final submission guard independently from its enablement rule.
            await CaseAsync(new string('界', 86), null, checks, password: true, forceSubmit: true);
            foreach (var password in new[] { " ", "\t", " \t ", "\u00a0", "  raw-pässword-12-34 \t", "12-AB-34",
                new string('界', 85), new string('é', 128), string.Concat(Enumerable.Repeat("😀", 64)) })
                await CaseAsync(password, password, checks, password: true);
            // The plaintext getter loses the NUL suffix, while SecurePassword
            // retains all assigned units. Neither enablement nor a forced
            // submission may silently authenticate with that shortened secret.
            await CaseAsync("before\0after", null, checks, password: true, inserted: "before");
            await CaseAsync("before\0after", null, checks, password: true, inserted: "before", forceSubmit: true);
            foreach (var password in new[] { "", new string('x', 257), "before\rafter", "before\nafter", "\ud800", "\udc00" })
                await CaseAsync(password, null, checks, password: true);
            await CaseAsync(new string('x', 256), new string('x', 256), checks, password: true, input: InputMode.Typed);
            foreach (var completion in new[] { Completion.Cancel, Completion.Stop, Completion.Timeout,
                Completion.IpcError, Completion.EndOfStream, Completion.Stopped })
                await CaseAsync("raw-password", "raw-password", checks, password: true, completion: completion);
        }
        finally { _loaded = null; }
    }

    private static async Task PreCancelledAsync(List<string> checks)
    {
        var owner = Owner(); owner.Show();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var opened = false;
        _loaded = dialog => { if (ReferenceEquals(dialog.Owner, owner)) opened = true; };
        try
        {
            try
            {
                await PinDialog.RequestAsync(owner, Receiver(false), cancellation.Token).WaitAsync(Limit);
                throw new InvalidOperationException("A pre-cancelled PIN request completed successfully.");
            }
            catch (OperationCanceledException)
            {
                Check(!opened, "pre-cancelled pairing never opens a modal dialog", checks);
            }
        }
        finally { _loaded = null; owner.Close(); }
    }

    private static Window Owner() => new()
    {
        Width = 320, Height = 100, Title = "Synthetic pairing verification", ShowInTaskbar = false
    };
    private static Receiver Receiver(bool password) => new("synthetic-pin", "Synthetic receiver", "192.0.2.10", 7001)
    { RequiresPassword = password };

    private static async Task CaseAsync(string text, string? expected, List<string> checks, bool password = false,
        Completion completion = Completion.Submit, InputMode input = InputMode.Assigned, string? inserted = null, bool forceSubmit = false)
    {
        var factory = new PairingFactory(password); var audio = new Audio();
        var timing = new SessionTiming(Limit, Limit,
            completion == Completion.Timeout ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30), TimeSpan.Zero);
        await using var controller = new SessionController(factory, audio, timing: timing, sessionRecords: _ => { });
        var owner = Owner(); owner.Show();
        var handled = Signal(); var terminal = Signal(); var closed = Signal();
        PinDialog? prompt = null; Task? stop = null; var actionStarted = false;
        var label = (password ? "password" : "PIN") + "/" + input + "/" + completion + (forceSubmit ? "/forced-submit" : "");
        controller.Changed += snapshot =>
        {
            if (Volatile.Read(ref actionStarted) && snapshot.State is PlaybackState.Streaming or PlaybackState.Error or PlaybackState.Idle)
                terminal.TrySetResult();
        };
        _loaded = dialog =>
        {
            if (!ReferenceEquals(dialog.Owner, owner)) return;
            prompt = dialog; dialog.Closed += (_, _) => closed.TrySetResult();
            try
            {
                var pair = UiSmoke.Descendants(dialog).OfType<Button>().Single(button => button.IsDefault);
                var cancel = UiSmoke.Descendants(dialog).OfType<Button>().Single(button => button.IsCancel);
                string? passwordObservation = null;
                if (password)
                {
                    var box = UiSmoke.Descendants(dialog).OfType<PasswordBox>().Single();
                    Check(!UiSmoke.Descendants(dialog).OfType<TextBox>().Any() && box.MaxLength == 0 && !pair.IsEnabled,
                        "password prompt uses only the secure password control and starts disabled", checks);
                    if (input == InputMode.Typed)
                    {
                        // Use a routed event with an in-memory data object. The editor
                        // must reject the original value before single-line paste truncation.
                        foreach (var (sample, rejected) in new[] { ("valid password", false), ("before\nafter", true), (new string('界', 86), true) })
                        {
                            var data = new DataObject(DataFormats.UnicodeText, sample);
                            var paste = new DataObjectPastingEventArgs(data, false, DataFormats.UnicodeText);
                            box.RaiseEvent(paste);
                            Check(paste.CommandCancelled == rejected, "password paste validates the complete selected text", checks);
                        }
                        Keyboard.Focus(box); Type(box, text);
                        Check(box.Password == text && pair.IsEnabled, "password typing reaches the 256-byte boundary", checks);
                        Type(box, "y");
                        Check(box.Password == text + "y" && !pair.IsEnabled, "overlong typing is rejected without truncating to a valid password", checks);
                        box.Password = text;
                    }
                    else box.Password = text;
                    var actual = box.Password;
                    using var secure = box.SecurePassword;
                    // PasswordBox can transform assigned text. Record only
                    // shape metadata for these synthetic values when an
                    // assertion fails; never infer its returned text from the
                    // original assignment or change the expected contract.
                    passwordObservation = $"assigned[{PasswordShape(text)}]; control[{PasswordShape(actual)}]; secure_units={secure.Length}; retained={actual == text}; enabled={pair.IsEnabled}; expected_enabled={expected is not null}";
                    if (input == InputMode.Assigned)
                    {
                        Check(actual == (inserted ?? text), label + " actual password control value matches its explicit assignment expectation", checks, passwordObservation);
                        Check(secure.Length == text.Length, label + " secure password copy retains every assigned UTF-16 unit", checks, passwordObservation);
                    }
                }
                else
                {
                    var box = UiSmoke.Descendants(dialog).OfType<TextBox>().Single();
                    Check(!UiSmoke.Descendants(dialog).OfType<PasswordBox>().Any() && box.MaxLength == 16 && !pair.IsEnabled,
                        "PIN prompt uses only the bounded PIN control and starts disabled", checks);
                    if (input == InputMode.Typed) VerifyTypedEditing(box, pair, checks);
                    else if (input == InputMode.Clipboard)
                    {
                        RequireDisposableForkRunner(); System.Windows.Clipboard.SetText(text); box.Paste();
                        Check(box.Text == inserted, "real PIN paste applies the single-line and sixteen-character editing bounds", checks);
                    }
                    else
                        // Assignment tests TextChanged validation after replacement;
                        // actual typing/paste bounds are asserted separately.
                        box.Text = text;
                }
                Check(pair.IsEnabled == (expected is not null), label + " enablement follows the resulting input", checks, passwordObservation);
                Volatile.Write(ref actionStarted, true);
                if (forceSubmit)
                {
                    // Dispatch the real Click handler synchronously, bypassing the
                    // button's enablement gate. A rejected submission leaves the
                    // modal open, so the fixture must dismiss it explicitly.
                    pair.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, pair));
                    Check(dialog.IsVisible && dialog.DialogResult is null && !closed.Task.IsCompleted
                        && controller.Snapshot.State == PlaybackState.Pairing
                        && !factory.Commands.Any(command => command.Text("command") is "pair_pin" or "start"),
                        label + " rejects forced submission and leaves the modal open", checks);
                    Invoke(cancel);
                }
                else if (completion == Completion.Stop) stop = controller.StopAsync();
                else if (completion is Completion.IpcError or Completion.EndOfStream or Completion.Stopped)
                    factory.Connections.First().Fail(completion);
                else if (completion != Completion.Timeout) Invoke(completion == Completion.Cancel || expected is null ? cancel : pair);
                handled.TrySetResult();
            }
            catch (Exception error) { handled.TrySetException(error); dialog.Close(); }
        };
        try
        {
            await controller.PairAsync(Receiver(password), new AppSettings { MuteWhileStreaming = false, ForceReconnect = false },
                (receiver, token) => PinDialog.RequestAsync(owner, receiver, token));
            await handled.Task.WaitAsync(Limit);
            if (stop is not null) await stop.WaitAsync(Limit);
            else await terminal.Task.WaitAsync(Limit);
            await closed.Task.WaitAsync(Limit);
            Check(prompt is { IsVisible: false }, label + " closes its actual modal dialog", checks);
            var commands = factory.Commands.ToArray();
            var pairCommand = commands.Single(command => command.Text("command") == "pair").GetProperty("params");
            Check(pairCommand.GetProperty("password_required").GetBoolean() == password &&
                pairCommand.GetProperty("peer").Integer("port") == 7001,
                label + " preserves the prompt kind and exact synthetic endpoint", checks);
            var pins = commands.Where(command => command.Text("command") == "pair_pin").ToArray();
            if (expected is not null && completion == Completion.Submit)
            {
                Check(controller.Snapshot.State == PlaybackState.Streaming && pins.Length == 1 &&
                    pins[0].GetProperty("params").Text("pin") == expected,
                    label + " sends exactly the expected normalized PIN or raw password", checks);
                Check(factory.Opens == 2 && commands.Count(command => command.Text("command") == "start") == 1,
                    label + " starts playback once in a separate fake process", checks);
            }
            else
            {
                await factory.Connections.First().Disposed.Task.WaitAsync(Limit);
                var failed = completion is Completion.Timeout or Completion.IpcError or Completion.EndOfStream or Completion.Stopped;
                Check(controller.Snapshot.State == (failed ? PlaybackState.Error : PlaybackState.Idle) &&
                    pins.Length == 0 && factory.Opens == 1 && !commands.Any(command => command.Text("command") == "start"),
                    label + " submits no credential and starts no playback", checks);
                if (completion == Completion.IpcError)
                    Check(controller.Snapshot.Diagnostics?.LastFault is { Code: "command_error", Channel: "ipc", Retryable: false,
                        Message: "synthetic modal rejection" }, label + " retains the terminal process error after closing the prompt", checks);
                if (completion is Completion.EndOfStream or Completion.Stopped)
                    Check(controller.Snapshot.Diagnostics?.LastFault?.Message == L.Get(completion == Completion.EndOfStream
                        ? "The audio engine exited." : "The audio session ended unexpectedly."),
                        label + " preserves the terminal cause rather than waiting for the longer input timeout", checks);
            }
            Check(audio.Mutes == 0, label + " never mutates a real or fake mute endpoint", checks);
        }
        finally
        {
            _loaded = null;
            try { await controller.StopAsync().WaitAsync(Limit); }
            finally { if (prompt is { IsVisible: true }) prompt.Close(); owner.Close(); }
        }
    }

    private static void VerifyTypedEditing(TextBox box, Button pair, List<string> checks)
    {
        Keyboard.Focus(box); Type(box, "1");
        Check(box.Text == "1" && !pair.IsEnabled, "partial typed PIN keeps Pair disabled", checks);
        Type(box, "2-3");
        Check(box.Text == "12-3" && !pair.IsEnabled, "typed dashes do not count as PIN digits", checks);
        Type(box, "4");
        Check(box.Text == "12-34" && pair.IsEnabled, "typed dashed four-digit PIN enables Pair", checks);
        foreach (var rejected in new[] { "a", "１", "١", " ", "\n", "−" })
        {
            Type(box, rejected);
            Check(box.Text == "12-34" && pair.IsEnabled, "typing rejects non-ASCII PIN input without changing valid text", checks);
        }
        box.SelectAll(); Type(box, "12345678");
        Check(box.Text == "12345678" && pair.IsEnabled, "typing enables eight PIN digits", checks);
        Type(box, "9");
        Check(box.Text == "123456789" && !pair.IsEnabled, "typing a ninth PIN digit disables Pair", checks);
        EditingCommands.Backspace.Execute(null, box);
        Check(box.Text == "12345678" && pair.IsEnabled, "Backspace restores valid PIN enablement", checks);
        box.SelectAll(); Type(box, "--------1234----"); Type(box, "-");
        Check(box.Text == "--------1234----" && pair.IsEnabled, "typing observes the sixteen-character PIN editing bound", checks);
        box.Select(8, 4); Type(box, "5678");
        Check(box.Text == "--------5678----" && pair.IsEnabled, "selection replacement works at the PIN editing bound", checks);
        box.SelectAll(); Type(box, "12-34");
    }
    private static void Type(IInputElement box, string text)
    {
        var composition = new TextComposition(InputManager.Current, box, text, TextCompositionAutoComplete.Off);
        TextCompositionManager.StartComposition(composition); TextCompositionManager.CompleteComposition(composition);
    }
    private static void RequireDisposableForkRunner()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "ThatMarko/AirFlash")
            throw new InvalidOperationException("Clipboard pairing verification requires the disposable ThatMarko/AirFlash hosted runner.");
    }
    private static IDataObject? SnapshotClipboard()
    {
        var original = System.Windows.Clipboard.GetDataObject();
        if (original is null) return null;
        var snapshot = new DataObject();
        foreach (var format in original.GetFormats(autoConvert: false))
            if (original.GetData(format, autoConvert: false) is { } value) snapshot.SetData(format, value, false);
        return snapshot;
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static string PasswordShape(string value)
    {
        int? bytes = null, malformedAt = null;
        try { bytes = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException error) { malformedAt = error.Index; }
        return $"utf16={value.Length},utf8={bytes?.ToString() ?? "invalid"},malformed_at={malformedAt?.ToString() ?? "none"},cr={value.Contains('\r')},lf={value.Contains('\n')},nul={value.Contains('\0')},replacement_count={value.Count(character => character == '\ufffd')}";
    }
    private static void Check(bool condition, string message, List<string> checks, string? detail = null)
    { if (!condition) throw new InvalidOperationException(message + (detail is null ? "" : ": " + detail)); checks.Add(message); }

    private sealed class Audio : IAudioService
    {
        public int Mutes;
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("synthetic-render");
        public Task MuteAsync(string? endpointId) { Mutes++; return Task.CompletedTask; }
        public Task RestoreAsync() => Task.CompletedTask;
    }
    private sealed class PairingFactory(bool password) : IEngineFactory
    {
        public readonly ConcurrentQueue<JsonElement> Commands = new();
        public readonly ConcurrentQueue<Connection> Connections = new();
        public int Opens => Connections.Count;
        public IEngineConnection Open() { var connection = new Connection(this, password); Connections.Enqueue(connection); return connection; }
    }
    private sealed class Connection(PairingFactory factory, bool password) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private string _session = "";
        public TaskCompletionSource Disposed { get; } = Signal();
        public void Fail(Completion completion)
        {
            if (completion == Completion.EndOfStream) _events.Writer.TryComplete();
            else if (completion == Completion.IpcError) _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new
            {
                version = 1, id = "", session_id = "", @event = "error", code = "command_error", channel = "ipc",
                host = (string?)null, retryable = false, message = "synthetic modal rejection"
            }));
            else _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = _session, @event = "stopped" }));
        }
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            _session = session;
            factory.Commands.Enqueue(JsonSerializer.SerializeToElement(new { command, session_id = session, @params = parameters }));
            if (command == "hello") _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new
            {
                version = 1, session_id = session, @event = "hello",
                capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "receiver_password", "receiver_ownership_guard" }
            }));
            var name = command switch { "pair" => password ? "password_required" : "pin_required", "pair_pin" => "paired", "start" => "streaming", _ => null };
            if (name is not null) _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session, @event = name }));
            if (command == "stop") _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        { try { return await _events.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; } }
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
