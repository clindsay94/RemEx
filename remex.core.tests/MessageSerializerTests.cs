using Remex.Core.Messages;

namespace Remex.Core.Tests;

public class MessageSerializerTests
{
    [Fact]
    public void Serialize_PingMessage_ProducesValidJson()
    {
        var msg = new RemexMessage { Type = MessageTypes.Ping, Timestamp = 12345 };
        var bytes = MessageSerializer.Serialize(msg);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"type\"", json);
        Assert.Contains("ping", json);
        Assert.Contains("12345", json);
    }

    [Fact]
    public void RoundTrip_PingMessage_PreservesAllFields()
    {
        var original = new RemexMessage { Type = MessageTypes.Ping, Timestamp = 99999 };
        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Type, deserialized!.Type);
        Assert.Equal(original.Timestamp, deserialized.Timestamp);
    }

    [Fact]
    public void RoundTrip_PongMessage_PreservesAllFields()
    {
        var original = new RemexMessage { Type = MessageTypes.Pong, Timestamp = 42 };
        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.Pong, deserialized!.Type);
        Assert.Equal(42, deserialized.Timestamp);
    }

    [Fact]
    public void RoundTrip_NullTimestamp_IsPreserved()
    {
        var original = new RemexMessage { Type = MessageTypes.Ping };
        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.Timestamp);
    }

    [Fact]
    public void Deserialize_MalformedJson_ReturnsNull()
    {
        var garbage = System.Text.Encoding.UTF8.GetBytes("not valid json {{{");
        var result = MessageSerializer.Deserialize(garbage);
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_EmptyBytes_ReturnsNull()
    {
        var result = MessageSerializer.Deserialize(ReadOnlySpan<byte>.Empty);
        Assert.Null(result);
    }
}
