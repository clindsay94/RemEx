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

    /// <summary>
    /// The PC's machine name rides host capabilities as camelCase <c>machineName</c>, so the phone can
    /// label an un-nicknamed known PC by name instead of IP (RemEx-odqj5).
    /// </summary>
    [Fact]
    public void HostInfoMessage_RoundTripsMachineNameAsCamelCase()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.HostInfo,
            HostCapabilities = new HostCapabilities { MachineName = "CONNOR-DESKTOP" },
        };

        var bytes = MessageSerializer.Serialize(msg);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        var deserialized = MessageSerializer.Deserialize(bytes);

        // The Kotlin parser reads this exact key; a rename here silently strands every phone.
        Assert.Contains("\"machineName\":\"CONNOR-DESKTOP\"", json, StringComparison.Ordinal);
        Assert.Equal("CONNOR-DESKTOP", deserialized!.HostCapabilities!.MachineName);
    }

    /// <summary>
    /// An older PC sends no <c>machineName</c>; the field must read back as null, never a placeholder,
    /// so the phone falls back to the address rather than showing a fake name.
    /// </summary>
    [Fact]
    public void HostInfoMessage_WithoutMachineName_ReadsBackNull()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.HostInfo,
            HostCapabilities = new HostCapabilities { Platform = "windows" },
        };

        var json = System.Text.Encoding.UTF8.GetString(MessageSerializer.Serialize(msg))
            .Replace("\"machineName\":null,", "", StringComparison.Ordinal)
            .Replace(",\"machineName\":null", "", StringComparison.Ordinal);
        Assert.DoesNotContain("machineName", json, StringComparison.Ordinal);

        var deserialized = MessageSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.NotNull(deserialized!.HostCapabilities);
        Assert.Equal("windows", deserialized.HostCapabilities!.Platform);
        Assert.Null(deserialized.HostCapabilities.MachineName);
    }
}
