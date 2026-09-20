using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace AirFlash.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Notify(property); return true;
    }
    protected void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
