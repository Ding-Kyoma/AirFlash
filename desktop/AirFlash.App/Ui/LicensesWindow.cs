using AirFlash.Core;
using AirFlash.App.Services;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
namespace AirFlash.App.Ui;

public sealed class LicensesWindow : Window
{
    public LicensesWindow()
    {
        Title = L.Get("Third-party licenses"); Width = 720; Height = 540;
        WindowThemeService.Track(this);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AirFlash.ThirdPartyNotices.txt")!;
        using var reader = new StreamReader(stream);
        Content = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(18) };
    }
}
