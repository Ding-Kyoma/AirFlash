using System.Collections.Concurrent;
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

internal static class UiPairingInputs
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(6);
    private enum InputMode { Assigned, Typed, Clipboard }
    public static async Task RunAsync(List<string> checks, bool useClipboard = false)
    {
        // Clipboard access is opt-in and belongs only to the disposable fork runner.
        // The ordinary local smoke fixture never reads or writes the clipboard.
        if (useClipboard) RequireForkRunner();
        var clipboard = useClipboard ? SnapshotClipboard() : null;
        try
        {
            await PreCancelledAsync(checks);
            await CaseAsync("12-34", "1234", cancel: false, stop: false, checks, InputMode.Typed);
            foreach (var (text, pin) in new (string, string?)[]
            {
                ("12-34", "1234"), ("1234", "1234"), ("12345678", "12345678"),
                ("12-34-56-78", "12345678"), ("--------1234----", "1234"),
                ("", null), ("123", null), ("123456789", null), ("123a", null),
                ("12 34", null), (" 1234", null), ("1234\n", null),
                ("１２３４", null), ("١٢٣٤", null), ("12−34", null),
                ("-------------1234", null)
            }) await CaseAsync(text, pin, cancel: false, stop: false, checks);
            await CaseAsync("1234", "1234", cancel: true, stop: false, checks);
            await CaseAsync("1234", "1234", cancel: false, stop: true, checks);
            if (useClipboard)
                foreach (var (text, inserted, pin) in new (string, string, string?)[]
                {
                    ("12-34", "12-34", "1234"), ("1234", "1234", "1234"),
                    ("12345678", "12345678", "12345678"),
                    ("12-34-56-78", "12-34-56-78", "12345678"),
                    ("--------1234----", "--------1234----", "1234"),
                    ("123", "123", null), ("123456789", "123456789", null),
                    ("123a", "123a", null), ("12 34", "12 34", null),
                    (" 1234", " 1234", null), ("１２３４", "１２３４", null),
                    ("١٢٣٤", "١٢٣٤", null), ("12−34", "12−34", null),
                    ("-------------1234", "-------------123", null),
                    // A single-line TextBox pastes only the prefix before a line break.
                    ("12\n34", "12", null), ("1234\n", "1234", "1234")
                }) await CaseAsync(text, pin, cancel: false, stop: false, checks, InputMode.Clipboard, inserted);
        }
        finally
        {
            if (useClipboard)
            {
                if (clipboard is null) System.Windows.Clipboard.Clear();
                else System.Windows.Clipboard.SetDataObject(clipboard, copy: true);
            }
        }
    }

    private static async Task PreCancelledAsync(List<string> checks)
    {
        var owner = Owner();
        owner.Show();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var opened = false;
        try
        {
            try
            {
                await PinDialog.RequestAsync(owner,
                    new("synthetic-pin", "Synthetic receiver", "192.0.2.10", 7000),
                    cancellation.Token, _ => opened = true).WaitAsync(Limit);
                throw new InvalidOperationException("A pre-cancelled PIN request completed successfully");
            }
            catch (OperationCanceledException)
            {
                Check(!opened, "A pre-cancelled PIN request does not open a modal dialog", checks);
            }
        }
        finally { owner.Close(); }
    }

    private static Window Owner()
        => new() { Width = 320, Height = 100, Title = "Synthetic pairing verification", ShowInTaskbar = false };

    private static async Task CaseAsync(string text, string? pin, bool cancel, bool stop, List<string> checks,
        InputMode input = InputMode.Assigned, string? inserted = null)
    {
        var factory = new PairingFactory();
        await using var controller = new SessionController(factory, new UiSmoke.MockAudio());
        var owner = Owner();
        owner.Show();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionStarted = false;
        PinDialog? prompt = null;
        Task? stopTask = null;
        controller.Changed += snapshot =>
        {
            if (Volatile.Read(ref actionStarted) && (snapshot.State is PlaybackState.Streaming or PlaybackState.Error or PlaybackState.Idle))
                terminal.TrySetResult();
        };
        try
        {
            await controller.PairAsync(new("synthetic-pin", "Synthetic receiver", "192.0.2.10", 7000),
                new AppSettings { MuteWhileStreaming = false }, (receiver, token) =>
                    PinDialog.RequestAsync(owner, receiver, token, dialog =>
                    {
                        prompt = dialog;
                        dialog.Closed += (_, _) => closed.TrySetResult();
                        try
                        {
                            var box = UiSmoke.Descendants(dialog).OfType<TextBox>().Single();
                            var pair = UiSmoke.Descendants(dialog).OfType<Button>().Single(button => button.IsDefault);
                            var dismiss = UiSmoke.Descendants(dialog).OfType<Button>().Single(button => button.IsCancel);
                            Check(box.MaxLength == 16, "PIN input retains the sixteen-character edit limit", checks);
                            if (input == InputMode.Typed) VerifyTypedEditing(box, pair, checks);
                            else if (input == InputMode.Clipboard)
                            {
                                RequireForkRunner();
                                System.Windows.Clipboard.SetText(text);
                                box.Paste();
                                Check(box.Text == inserted, "The runner PIN textbox applies its real paste and edit limits", checks);
                            }
                            else
                                // This deliberately bypasses input filtering to test TextChanged
                                // validation after text replacement; it does not execute a paste.
                                box.Text = text;
                            Check(pair.IsEnabled == (pin is not null), input switch
                            {
                                InputMode.Clipboard => "PIN clipboard-paste enablement follows the resulting normalized ASCII length",
                                InputMode.Typed => "PIN typed-input enablement follows normalized ASCII length",
                                _ => "PIN text-replacement enablement follows normalized ASCII length"
                            }, checks);
                            Volatile.Write(ref actionStarted, true);
                            if (stop) stopTask = controller.StopAsync();
                            else Invoke(cancel || pin is null ? dismiss : pair);
                            handled.TrySetResult();
                        }
                        catch (Exception error) { handled.TrySetException(error); dialog.Close(); }
                    }));
            await handled.Task.WaitAsync(Limit);
            if (stopTask is not null) await stopTask.WaitAsync(Limit);
            else await terminal.Task.WaitAsync(Limit);
            await closed.Task.WaitAsync(Limit);
            Check(prompt is { IsVisible: false }, "Every PIN submission, dismissal and cancellation closes its modal dialog", checks);
            var pins = factory.Commands.Where(command => command.Text("command") == "pair_pin").ToArray();
            if (pin is not null && !cancel && !stop)
            {
                Check(controller.Snapshot.State == PlaybackState.Streaming,
                    "An accepted synthetic PIN reaches Streaming rather than Error", checks);
                Check(pins.Length == 1 && pins[0].GetProperty("params").Text("pin") == pin,
                    "The modal dialog sends its normalized PIN in the real pair_pin command", checks);
                Check(factory.Commands.Count(command => command.Text("command") == "start") == 1,
                    "Successful synthetic pairing advances to playback once", checks);
            }
            else
            {
                Check(controller.Snapshot.State == PlaybackState.Idle,
                    "Invalid, dismissed and cancelled PIN input leaves the controller Idle", checks);
                Check(pins.Length == 0 && !factory.Commands.Any(command => command.Text("command") == "start"),
                    "Invalid, dismissed or cancelled PIN input sends no PIN or playback command", checks);
            }
        }
        finally
        {
            try { await controller.StopAsync(); }
            finally { if (prompt is { IsVisible: true }) prompt.Close(); owner.Close(); }
        }
    }

    private static void VerifyTypedEditing(TextBox box, Button pair, List<string> checks)
    {
        Keyboard.Focus(box);
        Type(box, "1");
        Check(box.Text == "1" && !pair.IsEnabled, "PIN typing inserts a partial ASCII PIN and keeps Pair disabled", checks);
        Type(box, "2-3");
        Check(box.Text == "12-3" && !pair.IsEnabled, "PIN typing preserves dashes without enabling three digits", checks);
        Type(box, "4");
        Check(box.Text == "12-34" && pair.IsEnabled, "PIN typing enables the four-digit boundary after dashed insertion", checks);
        foreach (var rejected in new[] { "a", "１", "١", " ", "\n", "−" })
        {
            Type(box, rejected);
            Check(box.Text == "12-34" && pair.IsEnabled, "PIN typing rejects letters, Unicode digits and whitespace without changing valid text", checks);
        }
        box.SelectAll(); Type(box, "12345678");
        Check(box.Text == "12345678" && pair.IsEnabled, "PIN typing replaces selected text and enables the eight-digit boundary", checks);
        Type(box, "9");
        Check(box.Text == "123456789" && !pair.IsEnabled, "PIN typing disables Pair when a ninth digit is inserted", checks);
        EditingCommands.Backspace.Execute(null, box);
        Check(box.Text == "12345678" && pair.IsEnabled, "PIN backspace restores the valid eight-digit boundary", checks);
        box.SelectAll(); Type(box, "--------1234----");
        Check(box.Text == "--------1234----" && pair.IsEnabled, "PIN typing accepts a valid dashed value at the sixteen-character edit limit", checks);
        Type(box, "-");
        Check(box.Text == "--------1234----" && pair.IsEnabled, "PIN typing cannot insert a seventeenth character", checks);
        box.Select(8, 4); Type(box, "5678");
        Check(box.Text == "--------5678----" && pair.IsEnabled, "PIN typing can replace selected digits at the edit limit", checks);
        box.SelectAll(); Type(box, "12-34");
        Check(box.Text == "12-34" && pair.IsEnabled, "The typed dashed PIN remains ready for actual submission", checks);
    }

    private static void Type(TextBox box, string text)
    {
        var composition = new TextComposition(InputManager.Current, box, text, TextCompositionAutoComplete.Off);
        TextCompositionManager.StartComposition(composition);
        TextCompositionManager.CompleteComposition(composition);
    }

    private static void RequireForkRunner()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.Ordinal)
            || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"), "ThatMarko/AirFlash", StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic clipboard pairing verification requires the disposable ThatMarko/AirFlash GitHub Actions runner");
    }

    private static IDataObject? SnapshotClipboard()
    {
        var original = System.Windows.Clipboard.GetDataObject();
        if (original is null) return null;
        // Materialize advertised formats before replacement, including delayed data.
        var snapshot = new DataObject();
        foreach (var format in original.GetFormats(autoConvert: false))
            if (original.GetData(format, autoConvert: false) is { } value)
                snapshot.SetData(format, value, false);
        return snapshot;
    }

    private static void Invoke(Button button)
        => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Check(bool condition, string message, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks.Add(message);
    }
    private sealed class PairingFactory : IEngineFactory
    {
        public readonly ConcurrentQueue<JsonElement> Commands = new();
        public IEngineConnection Open() => new Connection(this);
    }
    private sealed class Connection(PairingFactory factory) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            factory.Commands.Enqueue(JsonSerializer.SerializeToElement(new { command, session_id = session, @params = parameters }));
            var name = command switch { "pair" => "pin_required", "pair_pin" => "paired", "start" => "streaming", _ => null };
            if (name is not null) _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session, @event = name }));
            if (command == "stop") _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await _events.Reader.ReadAsync(cancellation); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync() { _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
