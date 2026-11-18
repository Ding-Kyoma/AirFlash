using System.Text;

namespace AirFlash.Core;

public static class PasswordInput
{
    private const int MaximumBytes = 256;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    // Match the native password contract without trimming or rewriting secrets.
    public static bool IsValid(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaximumBytes || password.IndexOfAny(['\r', '\n', '\0']) >= 0) return false;
        try { return StrictUtf8.GetByteCount(password) <= MaximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }
}
