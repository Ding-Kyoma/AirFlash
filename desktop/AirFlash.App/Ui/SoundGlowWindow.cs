using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
using AirFlash.Core;

namespace AirFlash.App.Ui;

// Deliberately session-only: closing this test window stops automatic changes.
public sealed class SoundGlowWindow : Window
{
    private readonly AppViewModel _app;
    private readonly TextBox _log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 160 };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _swatch = new() { Height = 60, CornerRadius = new CornerRadius(6), Background = Brushes.Gray, Margin = new Thickness(0, 12, 0, 12) };
    private readonly CheckBox _cycle = new() { Content = "每 15 秒随机换色（红 / 蓝 / 绿）", Margin = new Thickness(0, 8, 0, 8) };
    private readonly DispatcherTimer _timer;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private string? _lastPalette;
    private bool _busy, _closed;

    public SoundGlowWindow(AppViewModel app)
    {
        _app = app;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        Title = "AirFlash · Sound Glow Test"; Width = 610; Height = 660; MinWidth = 540; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(20) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new TextBlock { Text = "Sound Glow · 封面注入实验", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "先在家庭 App 打开 HomePod 的 Sound Glow，再用 AirFlash 播放音乐。按钮只发送虚构歌曲信息和测试封面，不直接控制 LED。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
        root.Children.Add(_state); root.Children.Add(_swatch); root.Children.Add(_actions);
        AddButton("仅歌曲信息", "metadata_only"); AddButton("红色", "red"); AddButton("蓝色", "blue"); AddButton("绿色", "green");
        root.Children.Add(_cycle);
        var reset = new Button { Content = "回到原始音频（停止并重连）", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
        reset.Click += async (_, _) => {
            if (_busy) return;
            _cycle.IsChecked = false; _timer.Stop(); _busy = true; UpdateState();
            try {
                var receiver = _app.Session.Snapshot.Receiver;
                await _app.Session.StopAsync();
                _lastPalette = null; _swatch.Background = Brushes.Gray;
                if (receiver is not null) await _app.Session.StartAsync(receiver, _app.Settings.Clone());
                Append("新会话不注入元数据。HomePod 可能暂时缓存旧封面；这里只恢复发送端基线，不是关闭设备上的 Sound Glow。");
            } catch (Exception error) { Append(error.Message); }
            finally { _busy = false; UpdateState(); }
        };
        root.Children.Add(reset);
        root.Children.Add(new TextBlock { Text = "建议顺序：原始音频 → 仅歌曲信息 → 红色 → 蓝色。封面发送后观察实际灯光。切回‘仅歌曲信息’不会清除旧封面，请先重连。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        root.Children.Add(_log);
        var logs = new Button { Content = "打开诊断日志", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        logs.Click += (_, _) => AppPaths.OpenLogs(); root.Children.Add(logs);
        _timer.Tick += async (_, _) => {
            var choices = new[] { "red", "blue", "green" }.Where(p => p != _lastPalette).ToArray();
            await SendAsync(choices[Random.Shared.Next(choices.Length)]);
        };
        _cycle.Checked += async (_, _) => { await SendAsync("red"); if (_cycle.IsChecked == true && !_closed) _timer.Start(); };
        _cycle.Unchecked += (_, _) => _timer.Stop();
        _app.Session.SoundGlowFeedback += OnFeedback; _app.Session.Changed += OnSessionChanged;
        Closed += (_, _) => { _closed = true; _timer.Stop(); _app.Session.SoundGlowFeedback -= OnFeedback; _app.Session.Changed -= OnSessionChanged; };
        WindowThemeService.Track(this); AppIconService.Track(this);
        UpdateState();
        Append("默认不发送封面。200/accepted 只表示请求被接收，不代表灯效已触发。关闭窗口停止随机换色，最后封面可能仍保留。");
    }
    private void AddButton(string label, string palette)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += async (_, _) => { _cycle.IsChecked = false; await SendAsync(palette); };
        _actions.Children.Add(button);
    }
    private async Task SendAsync(string palette)
    {
        if (_busy || _closed) return;
        _busy = true; UpdateState();
        try {
            await _app.Session.SendSoundGlowTestAsync(palette);
            _lastPalette = palette;
            _swatch.Background = palette switch { "red" => new SolidColorBrush(Color.FromRgb(240,24,24)), "blue" => new SolidColorBrush(Color.FromRgb(24,64,240)), "green" => new SolidColorBrush(Color.FromRgb(24,220,64)), _ => Brushes.Gray };
            Append($"已请求：{palette}；等待接收端响应。");
        } catch (Exception error) { _cycle.IsChecked = false; Append(error.Message); }
        finally { _busy = false; UpdateState(); }
    }
    private void OnFeedback(JsonElement item) => Dispatcher.BeginInvoke(() => {
        if (_closed) return;
        if (item.Text("event") == "sound_glow_queued") { Append("引擎已排队；尚未确认 HomePod 接收。"); return; }
        if (item.Text("event") == "sound_glow_error" || item.Boolean("accepted") == false) _cycle.IsChecked = false;
        Append(item.Text("event") == "sound_glow_result"
            ? $"#{item.Integer("sequence")} {item.Text("host")} · {item.Text("palette")} · {item.Text("part")}: {(item.Boolean("accepted") == true ? "200 接受（灯效待观察）" : "失败：" + item.Text("message"))}"
            : item.Text("message"));
    });
    private void OnSessionChanged(SessionSnapshot snapshot) => Dispatcher.BeginInvoke(() => {
        if (_closed) return;
        if (snapshot.State != PlaybackState.Streaming) { _cycle.IsChecked = false; _lastPalette = null; _swatch.Background = Brushes.Gray; }
        UpdateState();
    });
    private void UpdateState()
    {
        var streaming = _app.Session.Snapshot.State == PlaybackState.Streaming;
        _state.Text = $"播放状态：{_app.Session.Snapshot.State} · 请求的配色：{_lastPalette ?? "未发送"}";
        _actions.IsEnabled = streaming && !_busy; _cycle.IsEnabled = streaming && !_busy;
    }
    private void Append(string message)
    {
        if (_log.Text.Length > 16000) _log.Text = _log.Text[^8000..];
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}"); _log.ScrollToEnd();
    }
}
