using Remex.Core.Messages;

namespace Remex.Core.Tests;

public class RemexMessageTests
{
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
}
