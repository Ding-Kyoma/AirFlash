namespace AirFlash.App.Services;

internal enum TrayIdentity { Guid, WindowIconId }
internal enum TrayOperation : uint { Add = 0, Modify = 1, Delete = 2, SetVersion = 4 }
internal enum TrayAction { None, TogglePanel, OpenMenu }

// Keep the selected identity for the entire lifetime, including Explorer restarts.
// A GUID can be rejected when an unsigned executable moves to another directory.
internal sealed class TrayRegistration(
    Func<TrayOperation, TrayIdentity, bool> notify,
    Action<string> log,
    Func<DateTimeOffset>? clock = null,
    TrayIdentity? initialIdentity = null) : IDisposable
{
    private TrayIdentity? _selectedIdentity = initialIdentity;
    private readonly Dictionary<(string, TrayIdentity), DateTimeOffset> _failures = [];
    public TrayIdentity Identity => _selectedIdentity ?? TrayIdentity.Guid;
    public bool IsRegistered { get; private set; }
    public bool UsesVersion4 { get; private set; }
    public bool IsDisposed { get; private set; }

    public bool TryRegister()
    {
        if (IsDisposed) return false;
        if (IsRegistered) return true;
        if (TryAdd(Identity)) return true;
        return _selectedIdentity is null && TryAdd(TrayIdentity.WindowIconId);
    }

    private bool TryAdd(TrayIdentity identity)
    {
        if (!Send(TrayOperation.Add, identity)) return false;
        _selectedIdentity = identity;
        IsRegistered = true;
        UsesVersion4 = Send(TrayOperation.SetVersion, identity);
        return true;
    }

    public bool Update()
    {
        if (IsDisposed) return false;
        if (!IsRegistered) return TryRegister();
        if (Send(TrayOperation.Modify, Identity)) return true;
        // Remove any surviving entry before re-adding after a failed update.
        Send(TrayOperation.Delete, Identity);
        IsRegistered = false;
        UsesVersion4 = false;
        return TryRegister();
    }

    public bool TaskbarCreated()
    {
        if (IsDisposed) return false;
        IsRegistered = false;
        UsesVersion4 = false;
        return TryRegister();
    }

    private bool Send(TrayOperation operation, TrayIdentity identity)
    {
        var success = notify(operation, identity);
        ReportResult(operation.ToString(), success, identity);
        return success;
    }

    public void ReportResult(string operation, bool success, TrayIdentity identity, string? detail = null)
    {
        var key = (operation, identity);
        if (success)
        {
            var recovered = _failures.Remove(key);
            if (!recovered && operation is "Modify" or "GetRect") return;
        }
        else
        {
            var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
            if (_failures.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(30)) return;
            _failures[key] = now;
        }
        log($"operation={operation} identity={identity} success={success}{(detail is null ? "" : " " + detail)}");
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        if (_selectedIdentity is not null) Send(TrayOperation.Delete, Identity);
        IsRegistered = false;
        UsesVersion4 = false;
    }

    internal static TrayAction DecodeCallback(long wParam, long lParam, bool version4)
    {
        if (version4)
            return (lParam & 0xffff) switch
            {
                0x400 or 0x401 => TrayAction.TogglePanel,
                0x7b => TrayAction.OpenMenu,
                _ => TrayAction.None
            };
        if (wParam != 1) return TrayAction.None;
        return lParam switch
        {
            0x202 => TrayAction.TogglePanel,
            0x205 or 0x7b => TrayAction.OpenMenu,
            _ => TrayAction.None
        };
    }
}
