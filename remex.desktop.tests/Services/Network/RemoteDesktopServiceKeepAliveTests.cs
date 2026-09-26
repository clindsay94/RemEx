using System.Net.WebSockets;
using System.Reflection;
using FluentAssertions;
using Remex.Desktop.Services.Network;
using Xunit;

namespace Remex.Desktop.Tests.Services.Network;

/// <summary>
/// RemEx-kjk8f: <c>RemoteDesktopService</c>'s socket had no keep-alive configured at all — not even
/// an interval, let alone a timeout — so a Ping-less dead link read <see cref="WebSocketState.Open"/>
/// until the OS gave up on TCP retransmits (many minutes), the same half-open-stall class P0-12
/// (RemEx-4j8ls) fixed for <c>RemexDesktopClient</c> and RemEx-kjk8f itself then fixed for the sibling
/// <c>ConnectionViewModel</c> socket.
/// </summary>
public sealed class RemoteDesktopServiceKeepAliveTests
{
    [Fact]
    public void CreateClientWebSocket_SetsBothKeepAliveIntervalAndTimeout()
    {
        var service = new RemoteDesktopService();
        var method = typeof(RemoteDesktopService).GetMethod(
            "CreateClientWebSocket", BindingFlags.NonPublic | BindingFlags.Instance)!;

        using var socket = (ClientWebSocket)method.Invoke(service, null)!;

        socket.Options.KeepAliveInterval.Should().BeGreaterThan(TimeSpan.Zero);
        // Matches Remex.Core.Native.SocketLiveness.KeepAliveTimeout (internal to remex.core, not
        // visible here) so every RD socket in the process detects a drop on the same budget.
        socket.Options.KeepAliveTimeout.Should().Be(TimeSpan.FromSeconds(20),
            "a Ping with no answer must eventually abort the socket rather than stall forever");

        service.Dispose();
    }
}
