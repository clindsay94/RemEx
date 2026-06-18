using System.Collections.Generic;

namespace Remex.Host.Services.Session;

/// <summary>The action needed to make the signed-in user's session usable for remote control.</summary>
public enum SessionGuardAction { Reconnect, AlreadyUsable, NoUserSession }

/// <summary>A coarse mapping of the WTS connect-state values this policy cares about.</summary>
public enum WtsState { Active, Connected, Disconnected, Other }

/// <summary>A snapshot of one Windows session for the policy to reason about (no P/Invoke types).</summary>
public readonly record struct SessionSnapshot(uint SessionId, bool HasSignedInUser, WtsState State);

/// <summary>The decision and (when reconnecting) which session to reconnect to the console.</summary>
public readonly record struct SessionGuardDecision(SessionGuardAction Action, uint SessionId);

/// <summary>
/// Pure decision logic for the interactive session guard: given a snapshot of the machine's
/// sessions, decide whether the signed-in user's session is already usable, needs reconnecting to
/// the console (to resume it unlocked), or there is no signed-in user to act on. Kept free of
/// P/Invoke so it is fully unit-testable.
/// </summary>
public static class SessionGuardPolicy
{
    public static SessionGuardDecision Decide(IReadOnlyList<SessionSnapshot> sessions)
    {
        foreach (var s in sessions)
        {
            if (!s.HasSignedInUser)
            {
                continue;
            }

            // A user session already on the console is usable; otherwise reconnect it.
            var action = s.State is WtsState.Active or WtsState.Connected
                ? SessionGuardAction.AlreadyUsable
                : SessionGuardAction.Reconnect;
            return new SessionGuardDecision(action, s.SessionId);
        }

        return new SessionGuardDecision(SessionGuardAction.NoUserSession, 0);
    }
}
