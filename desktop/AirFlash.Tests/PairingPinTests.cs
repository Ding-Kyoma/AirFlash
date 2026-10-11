using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class PairingPinTests
{
    [Theory]
    [InlineData("1234", "1234")]
    [InlineData("0000", "0000")]
    [InlineData("12345678", "12345678")]
    [InlineData("12-34", "1234")]
    [InlineData("12-34-56-78", "12345678")]
    [InlineData("--------1234----", "1234")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("---", null)]
    [InlineData("123", null)]
    [InlineData("123456789", null)]
    [InlineData("123a", null)]
    [InlineData("12 34", null)]
    [InlineData(" 1234", null)]
    [InlineData("1234\n", null)]
    [InlineData("12−34", null)]
    [InlineData("１２３４", null)]
    [InlineData("١٢٣٤", null)]
    [InlineData("-------------1234", null)]
    public void NormalizesOnlyPermittedSeparatorsAndAsciiDigitLengths(string? input, string? expected)
        => Assert.Equal(expected, PairingPin.Normalize(input));
}
