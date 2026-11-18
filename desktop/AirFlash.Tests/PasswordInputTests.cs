using System.Text;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class PasswordInputTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" \t ")]
    [InlineData("\u00a0")]
    [InlineData("\u2003")]
    [InlineData("  raw-pässword-12-34 \t")]
    public void NonemptyWhitespaceAndUnicodePasswordsAreAccepted(string password)
        => Assert.True(PasswordInput.IsValid(password));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    [InlineData("before\rafter")]
    [InlineData("before\nafter")]
    [InlineData("before\0after")]
    public void MissingOrForbiddenInputCannotSubmit(string? password)
        => Assert.False(PasswordInput.IsValid(password));

    [Fact]
    public void MalformedUtf16CannotBeSilentlyReplacedForSubmission()
    {
        // Attribute strings are UTF-8 encoded in metadata; create the invalid
        // UTF-16 values at runtime so the fixture itself cannot replace them.
        foreach (var password in new[] { new string('\ud800', 1), new string('\udc00', 1),
            "before" + new string('\ud800', 1) + "after", new string(['\udc00', '\ud800']) })
            Assert.False(PasswordInput.IsValid(password));
    }

    public static TheoryData<string, int, bool> EncodedBoundaries => new()
    {
        { new string('x', 128), 128, true },
        { new string('x', 256), 256, true },
        { new string('x', 257), 257, false },
        { new string('界', 85), 255, true },
        { new string('界', 86), 258, false },
        { new string('é', 128), 256, true },
        { new string('é', 129), 258, false },
        { string.Concat(Enumerable.Repeat("😀", 64)), 256, true },
        { string.Concat(Enumerable.Repeat("😀", 65)), 260, false }
    };

    [Theory]
    [MemberData(nameof(EncodedBoundaries))]
    public void SubmissionUsesTheUtf8ByteLimit(string password, int encodedBytes, bool expected)
    {
        Assert.Equal(encodedBytes, Encoding.UTF8.GetByteCount(password));
        Assert.Equal(expected, PasswordInput.IsValid(password));
    }
}
