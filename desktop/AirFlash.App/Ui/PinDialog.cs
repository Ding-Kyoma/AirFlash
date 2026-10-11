using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.Ui;

public sealed class PinDialog : Window
{
    private readonly TextBox _code = new() { MaxLength = PairingPin.MaximumInputLength, FontSize = 24, HorizontalContentAlignment = HorizontalAlignment.Center };
    private PinDialog(Receiver receiver, Action<PinDialog>? opened = null)
    {
        Title = L.Get("Pair HomePod"); Width = 390; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowThemeService.Track(this);
        AppIconService.Track(this);
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = L.Format("Enter the PIN provided by {0} ({1})", receiver.Name, receiver.Address), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 18) });
        _code.PreviewTextInput += (_, args) => args.Handled = args.Text.Any(c => !char.IsAsciiDigit(c) && c != '-');
        panel.Children.Add(_code);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var ok = new Button { Content = L.Get("Pair"), IsDefault = true, IsEnabled = false, MinWidth = 78, Margin = new(0, 0, 8, 0) };
        _code.TextChanged += (_, _) => ok.IsEnabled = PairingPin.Normalize(_code.Text) is not null;
        ok.Click += (_, _) => { if (PairingPin.Normalize(_code.Text) is not null) DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = L.Get("Cancel"), IsCancel = true, MinWidth = 78 }); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => { _code.Focus(); opened?.Invoke(this); };
    }
    public static Task<string?> RequestAsync(Window owner, Receiver receiver, CancellationToken cancellation)
        => RequestAsync(owner, receiver, cancellation, null);
    internal static async Task<string?> RequestAsync(Window owner, Receiver receiver, CancellationToken cancellation, Action<PinDialog>? opened)
    {
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var dialog = new PinDialog(receiver, opened) { Owner = owner };
            using var registration = cancellation.Register(() => owner.Dispatcher.BeginInvoke(() => dialog.Close()));
            var result = dialog.ShowDialog() == true ? PairingPin.Normalize(dialog._code.Text) : null;
            cancellation.ThrowIfCancellationRequested();
            return result;
        });
    }
}
