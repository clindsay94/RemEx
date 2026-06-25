using System;
using Remex.Desktop.Services;
using Remex.Agent.Services.Session;

namespace Remex.Agent.Services;

/// <summary>
/// Windows implementation of <see cref="ISessionKeepUnlockedService"/>. Reads/writes the machine-wide
/// opt-in flag (<see cref="SessionGuardSettings"/>) consumed by the Session-0 service's interactive
/// session guard. Off-by-default; unsupported on non-Windows platforms. (RemEx-l6o)
/// </summary>
public sealed class SessionKeepUnlockedService : ISessionKeepUnlockedService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsEnabled() => IsSupported && SessionGuardSettings.IsKeepUnlockedEnabled();

    public bool SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            return false;
        }

        return SessionGuardSettings.SetKeepUnlockedEnabled(enabled);
    }
}
