using System.Runtime.InteropServices;
namespace AirFlash.App.Input;

/// <summary>
/// Presses the Windows media keys so the app that is actually playing (Spotify,
/// Music, a browser) reacts to the receiver's own buttons.
/// </summary>
/// <remarks>
/// A media key press is a global event: whichever app owns the media session sees
/// it, which is exactly what a remote pointed at the speaker should do. The
/// reference implementation goes the same way rather than reaching into the
/// MediaRemote COM API, which Windows does not expose. Failures are logged and
/// swallowed — a speaker button must never take the stream down.
/// </remarks>
internal static class MediaKeys
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint MAPVK_VK_TO_VSC = 0;
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] INPUT[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>Press and release one virtual key. Returns false when the input
    /// desktop refused the injection (for example on a locked session).</summary>
    public static bool Press(int virtualKey, Action<string>? log = null)
    {
        var scan = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
        var inputs = new[]
        {
            new INPUT { Type = INPUT_KEYBOARD, Data = new() { Keyboard = new() { VirtualKey = (ushort)virtualKey, ScanCode = scan } } },
            new INPUT { Type = INPUT_KEYBOARD, Data = new() { Keyboard = new() { VirtualKey = (ushort)virtualKey, ScanCode = scan, Flags = KEYEVENTF_KEYUP } } },
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length) return true;
        log?.Invoke($"Media key 0x{virtualKey:X2} was not delivered ({Marshal.GetLastWin32Error()}).");
        return false;
    }
}
