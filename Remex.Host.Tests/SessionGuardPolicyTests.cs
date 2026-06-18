using System.Collections.Generic;
using Remex.Host.Services.Session;
using Xunit;

namespace Remex.Host.Tests;

public class SessionGuardPolicyTests
{
    [Fact]
    public void NoSignedInUser_ReturnsNoUserSession()
    {
        var sessions = new List<SessionSnapshot>
        {
            new(0, false, WtsState.Connected), // console at logon screen, no user
        };
        var d = SessionGuardPolicy.Decide(sessions);
        Assert.Equal(SessionGuardAction.NoUserSession, d.Action);
    }

    [Fact]
    public void DisconnectedUserSession_ReturnsReconnectWithItsId()
    {
        var sessions = new List<SessionSnapshot>
        {
            new(0, false, WtsState.Connected),
            new(1, true, WtsState.Disconnected),
        };
        var d = SessionGuardPolicy.Decide(sessions);
        Assert.Equal(SessionGuardAction.Reconnect, d.Action);
        Assert.Equal(1u, d.SessionId);
    }

    [Fact]
    public void ActiveUserSession_ReturnsAlreadyUsable()
    {
        var sessions = new List<SessionSnapshot> { new(1, true, WtsState.Active) };
        var d = SessionGuardPolicy.Decide(sessions);
        Assert.Equal(SessionGuardAction.AlreadyUsable, d.Action);
    }

    [Fact]
    public void ConnectedUserSession_ReturnsAlreadyUsable()
    {
        var sessions = new List<SessionSnapshot> { new(2, true, WtsState.Connected) };
        var d = SessionGuardPolicy.Decide(sessions);
        Assert.Equal(SessionGuardAction.AlreadyUsable, d.Action);
    }

    [Fact]
    public void OtherStateUserSession_ReturnsReconnect()
    {
        var sessions = new List<SessionSnapshot> { new(3, true, WtsState.Other) };
        var d = SessionGuardPolicy.Decide(sessions);
        Assert.Equal(SessionGuardAction.Reconnect, d.Action);
        Assert.Equal(3u, d.SessionId);
    }
}
