using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class RemoteCommandTests
{
    [Fact]
    public async Task An_old_remote_command_cannot_regain_authority_after_standby_resume()
    {
        var connection = new SessionTestEngine.Connection();
        var reported = System.Threading.Channels.Channel.CreateUnbounded<RemoteCommandAuthority>();
        var limit = SessionTestEngine.Limit;
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);
        controller.OwnedRemoteCommandReceived += (_, authority) => reported.Writer.TryWrite(authority);
        await SessionTestEngine.StreamAsync(controller, connection);
        connection.Current("remote_command", new { commands = new[] { "mute" } });
        var original = await reported.Reader.ReadAsync().AsTask().WaitAsync(limit);

        connection.Current("standby");
        connection.Current("resuming");
        connection.Current("streaming");
        connection.Current("remote_command", new { commands = new[] { "mute" } });
        var resumed = await reported.Reader.ReadAsync().AsTask().WaitAsync(limit);

        Assert.Equal(original.Owner, resumed.Owner);
        Assert.Equal(original.SessionId, resumed.SessionId);
        Assert.False(await controller.TryToggleRemoteMuteAsync(original).WaitAsync(limit));
        Assert.True(await controller.TryToggleRemoteMuteAsync(resumed).WaitAsync(limit));
        Assert.True(controller.Muted);
        Assert.Equal(1, connection.Count("set_gain"));
    }

    [Fact]
    public async Task A_remote_mute_waiting_for_serialization_loses_authority_on_explicit_intent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<RemoteCommandAuthority>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new SessionTestEngine.Connection
        {
            BeforeCommandAsync = command =>
            {
                if (command != "set_gain") return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            }
        };
        var limit = SessionTestEngine.Limit;
        await using var controller = TestSessionController.Create(new SessionTestEngine.Factory(connection), new SessionTestEngine.Audio(), timing: SessionTestEngine.Timing);
        controller.OwnedRemoteCommandReceived += (_, authority) => reported.TrySetResult(authority);
        await SessionTestEngine.StreamAsync(controller, connection);
        connection.Current("remote_command", new { commands = new[] { "mute" } });
        var current = await reported.Task.WaitAsync(limit);
        var slider = controller.SetMutedAsync(false);
        try
        {
            await entered.Task.WaitAsync(limit);
            var remote = controller.TryToggleRemoteMuteAsync(current);
            Assert.False(remote.IsCompleted);
            controller.BeginExplicitIntent();
            release.TrySetResult();
            Assert.False(await remote.WaitAsync(limit));
            Assert.False(controller.Muted);
            Assert.Equal(1, connection.Count("set_gain"));
        }
        finally
        {
            release.TrySetResult();
            await slider.WaitAsync(limit);
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Commands_map_to_the_matching_media_keys()
    {
        Assert.Null(new RemoteCommand("play").VirtualKey);
        Assert.Null(new RemoteCommand("pause").VirtualKey);
        Assert.Equal(0xB3, new RemoteCommand("toggle").VirtualKey);
        Assert.Equal(0xB2, new RemoteCommand("stop").VirtualKey);
        Assert.Equal(0xB0, new RemoteCommand("next").VirtualKey);
        Assert.Null(new RemoteCommand("shuffle_next").VirtualKey);
        Assert.Null(new RemoteCommand("repeat_next").VirtualKey);
        Assert.Equal(0xB1, new RemoteCommand("previous").VirtualKey);
        Assert.Null(new RemoteCommand("volume_up").VirtualKey);
        Assert.Null(new RemoteCommand("mute").VirtualKey);
        Assert.Null(new RemoteCommand("something_new").VirtualKey);
    }
    [Fact]
    public void Input_layout_matches_the_native_union_without_injecting_keys()
        => Assert.Equal(IntPtr.Size == 8 ? 40 : 28, AirFlash.App.Input.MediaKeys.InputSize);

    [Fact]
    public void An_event_body_becomes_one_command_per_entry()
    {
        var commands = RemoteCommand.Parse(Json("""
            {"event":"remote_command","host":"192.168.1.20","commands":["next","volume_up"],"source":"event","volume":0.4}
            """));
        Assert.Equal(2, commands.Count);
        Assert.Equal("next", commands[0].Name);
        Assert.Equal(0xB0, commands[0].VirtualKey);
        Assert.Equal("192.168.1.20", commands[0].Host);
        Assert.Equal(0.4, commands[0].Volume);
        Assert.Null(commands[1].VirtualKey);
    }

    [Fact]
    public void A_volume_only_event_yields_one_logged_volume_command()
    {
        var command = Assert.Single(RemoteCommand.Parse(Json("""
            {"event":"remote_command","host":"192.168.1.20","commands":[],"source":null,"volume":0.5}
            """)));
        Assert.Equal(new RemoteCommand("volume", 0.5, "192.168.1.20"), command);
        // Logged only: the receiver's own level is read separately (device_volume).
        Assert.Equal(RemoteCommandTarget.None, command.Target);
        Assert.Equal(0, command.VolumeDelta);
    }

    [Theory]
    [InlineData("""{"commands":[],"volume":null}""")]
    [InlineData("""{"commands":[],"volume":1.5}""")]
    [InlineData("""{"commands":[],"volume":-0.1}""")]
    [InlineData("""{"commands":[],"volume":"0.5"}""")]
    [InlineData("""{"commands":[]}""")]
    public void An_event_without_a_command_or_a_valid_volume_yields_nothing(string body)
        => Assert.Empty(RemoteCommand.Parse(Json(body)));

    [Theory]
    [InlineData("play", RemoteCommandTarget.MediaSession, 0)]
    [InlineData("pause", RemoteCommandTarget.MediaSession, 0)]
    [InlineData("shuffle_next", RemoteCommandTarget.MediaSession, 0)]
    [InlineData("repeat_next", RemoteCommandTarget.MediaSession, 0)]
    [InlineData("toggle", RemoteCommandTarget.MediaKey, 0)]
    [InlineData("stop", RemoteCommandTarget.MediaKey, 0)]
    [InlineData("next", RemoteCommandTarget.MediaKey, 0)]
    [InlineData("previous", RemoteCommandTarget.MediaKey, 0)]
    [InlineData("mute", RemoteCommandTarget.Mute, 0)]
    [InlineData("volume_up", RemoteCommandTarget.MasterVolume, RemoteCommand.VolumeStep)]
    [InlineData("volume_down", RemoteCommandTarget.MasterVolume, -RemoteCommand.VolumeStep)]
    [InlineData("volume", RemoteCommandTarget.None, 0)]
    [InlineData("something_new", RemoteCommandTarget.None, 0)]
    public void Every_engine_command_has_one_destination(string name, RemoteCommandTarget target, int delta)
    {
        var command = new RemoteCommand(name);
        Assert.Equal(target, command.Target);
        Assert.Equal(delta, command.VolumeDelta);
        // Exactly the media-key commands carry a key.
        Assert.Equal(target == RemoteCommandTarget.MediaKey, command.VirtualKey is not null);
    }

    [Fact]
    public void A_malformed_body_yields_nothing_instead_of_throwing()
    {
        Assert.Empty(RemoteCommand.Parse(Json("""{"event":"remote_command"}""")));
        Assert.Empty(RemoteCommand.Parse(Json("""{"commands":"next"}""")));
        Assert.Empty(RemoteCommand.Parse(Json("""{"commands":[1,2]}""")));
        var withOne = RemoteCommand.Parse(Json("""{"commands":["stop",7,null]}"""));
        Assert.Single(withOne);
        Assert.Equal("stop", withOne[0].Name);
    }
}
