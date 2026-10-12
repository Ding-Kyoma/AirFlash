using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class AudioEndpointTests
{
    [Theory]
    [InlineData(AudioEndpointKind.Playback, "Playback")]
    [InlineData(AudioEndpointKind.Recording, "Recording")]
    public void DisplayLabelsPreserveRawNameAndDeviceIdentity(AudioEndpointKind kind, string label)
    {
        var endpoint = new AudioEndpoint("stable-id", "Voicemeeter Output", kind);
        Assert.Equal("Voicemeeter Output", endpoint.Name);
        Assert.Equal("stable-id", endpoint.Id);
        Assert.Equal($"Voicemeeter Output · {L.Get(label)}", endpoint.DisplayName);
        Assert.Equal(AudioEndpointKind.Playback, new AudioEndpoint("old", "Speaker").Kind);
    }

    [Fact]
    public void RecordingEndpointUsesExistingSettingsSchema()
    {
        var settings = new AppSettings { CaptureMode = "endpoint", CaptureEndpoint = "recording-id" };
        var restored = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, AppSettings.JsonOptions), AppSettings.JsonOptions)!;
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Null(restored.Validate());
        Assert.Equal("recording-id", restored.EffectiveEndpoint);
        Assert.True(restored.MuteWhileStreaming);
    }
}
