using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AirFlash.App.Services;
namespace AirFlash.App.Ui;

internal sealed class CoffeeWindow : Window
{
    public CoffeeWindow()
    {
        Title = "请作者喝一杯咖啡~"; Width = 420; Height = 720;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; UseLayoutRounding = true;
        WindowThemeService.Track(this);
        AppIconService.Track(this);
        SourceInitialized += (_, _) => NativeWindowPlacement.CenterDialog(this);

        var panel = new Grid { Margin = new(24) };
        panel.RowDefinitions.Add(new RowDefinition { Height = new(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var image = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/AirFlash;component/Assets/PayQrcode.png", UriKind.Absolute)),
            Stretch = Stretch.Uniform,
            Margin = new(0, 0, 0, 16)
        };
        System.Windows.Automation.AutomationProperties.SetName(image, "微信和支付宝收款二维码");
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        panel.Children.Add(image);
        var close = new Button { Content = "关闭", IsCancel = true, IsDefault = true, MinWidth = 78, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 1); panel.Children.Add(close); Content = panel;
        Loaded += (_, _) => close.Focus();
    }
}
