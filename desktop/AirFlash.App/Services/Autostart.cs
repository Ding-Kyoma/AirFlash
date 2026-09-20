using Microsoft.Win32;
namespace AirFlash.App.Services;

public interface IAutostart
{
    void Set(bool enabled);
    Task SetAsync(bool enabled) => Task.Run(() => Set(enabled));
}
public sealed class Autostart : IAutostart
{
    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled) key.SetValue(AppPaths.Name, $"\"{Environment.ProcessPath}\" --startup", RegistryValueKind.String);
        else key.DeleteValue(AppPaths.Name, false);
    }
}
