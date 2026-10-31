using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class RemoteCommandTests
{
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
