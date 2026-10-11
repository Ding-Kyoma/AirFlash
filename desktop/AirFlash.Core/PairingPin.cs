namespace AirFlash.Core;

public static class PairingPin
{
    public const int MaximumInputLength = 16;
    public static string? Normalize(string? input)
    {
        if (input is null || input.Length > MaximumInputLength) return null;
        foreach (var character in input)
            if (!char.IsAsciiDigit(character) && character != '-') return null;
        var digits = input.Replace("-", "", StringComparison.Ordinal);
        return digits.Length is >= 4 and <= 8 ? digits : null;
    }
}
