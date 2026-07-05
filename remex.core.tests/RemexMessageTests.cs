using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Core.Tests;

public class RemexMessageTests
{
    [Fact]
    public void RemexMessage_CommandType_SerializesCorrectly()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.Command,
            CommandAction = "Shutdown",
        };
        var bytes = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.Deserialize(bytes);
        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.Command, deserialized!.Type);
        Assert.Equal("Shutdown", deserialized.CommandAction);
    }

    [Fact]
    public void PingMessage_HasCorrectType()
    {
        var msg = new RemexMessage { Type = MessageTypes.Ping };
        Assert.Equal("ping", msg.Type);
    }

    [Fact]
    public void PongMessage_HasCorrectType()
    {
        var msg = new RemexMessage { Type = MessageTypes.Pong };
        Assert.Equal("pong", msg.Type);
    }

    [Fact]
    public void Message_TimestampIsOptional()
    {
        var msg = new RemexMessage { Type = MessageTypes.Ping };
        Assert.Null(msg.Timestamp);
    }

    [Fact]
    public void Message_TimestampCanBeSet()
    {
        var ts = DateTimeOffset.UtcNow.Ticks;
        var msg = new RemexMessage { Type = MessageTypes.Ping, Timestamp = ts };
        Assert.Equal(ts, msg.Timestamp);
    }

    [Fact]
    public void MessageTypes_ConstantsAreCorrect()
    {
        Assert.Equal("ping", MessageTypes.Ping);
        Assert.Equal("pong", MessageTypes.Pong);
    }

    [Fact]
    public void HostInfoMessage_RoundTripsCapabilities()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.HostInfo,
            HostCapabilities = new HostCapabilities
            {
                RuntimeMode = "service",
                Platform = "windows",
                SupportsRemoteDesktop = false,
                SupportsCursorQuery = false,
                SupportsAdvancedWindowControl = true,
                InputBackend = "xdotool",
                WindowControlBackend = "kdotool",
                RemoteDesktopUnavailableReason = "Interactive session required."
            }
        };

        var bytes = MessageSerializer.Serialize(msg);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.HostInfo, deserialized!.Type);
        Assert.NotNull(deserialized.HostCapabilities);
        Assert.False(deserialized.HostCapabilities!.SupportsRemoteDesktop);
        Assert.False(deserialized.HostCapabilities.SupportsCursorQuery);
        Assert.True(deserialized.HostCapabilities.SupportsAdvancedWindowControl);
        Assert.Equal("xdotool", deserialized.HostCapabilities.InputBackend);
        Assert.Equal("kdotool", deserialized.HostCapabilities.WindowControlBackend);
        Assert.Equal("Interactive session required.", deserialized.HostCapabilities.RemoteDesktopUnavailableReason);
    }
}
