using AirFlash.Core;
using AirFlash.App.Services;
using System.Net;
using System.Windows;
using System.Windows.Controls;
namespace AirFlash.App.Ui;

public sealed class ManualReceiverDialog : Window
{
    private readonly TextBox _name = new(), _host = new(), _port = new() { Text = "7000" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    public string DeviceName => _name.Text.Trim();
    public string Host => _host.Text.Trim();
    public int Port => int.Parse(_port.Text);
    public ManualReceiverDialog()
    {
        Title = L.Get("Add receiver manually"); Width = 390; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowThemeService.Track(this);
        var panel = new StackPanel { Margin = new(22) };
        foreach (var (label, input) in new[] { (L.Get("Name (optional)"), _name), (L.Get("IP address or hostname"), _host), (L.Get("Port"), _port) })
        { panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 6) }); input.Margin = new(0, 0, 0, 16); panel.Children.Add(input); }
        _error.SetResourceReference(ForegroundProperty, "ErrorBrush"); panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 14, 0, 0) };
        var cancel = new Button { Content = L.Get("Cancel"), IsCancel = true, MinWidth = 74 };
        var add = new Button { Content = L.Get("Add"), IsDefault = true, MinWidth = 74, Margin = new(0, 0, 8, 0) };
        add.Click += (_, _) =>
        {
            if (Uri.CheckHostName(Host) == UriHostNameType.Unknown || Uri.CheckHostName(Host) == UriHostNameType.IPv6 || !int.TryParse(_port.Text, out var port) || port is < 1 or > 65535) { _error.Text = L.Get("Enter a valid IPv4 address or hostname and a port between 1 and 65535."); return; }
            DialogResult = true;
        };
        buttons.Children.Add(add); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => _host.Focus();
    }
}
