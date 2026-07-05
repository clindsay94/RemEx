using System;
using Remex.Desktop.Services;
using Remex.Agent.Services.Session;

namespace Remex.Agent.Services;

/// <summary>
/// Windows implementation of <see cref="ISessionKeepUnlockedService"/>. Reads/writes the machine-wide
/// opt-in flag (<see cref="SessionGuardSettings"/>) consumed by the interactive session guard. When
/// enabled, RemEx keeps the signed-in session AWAKE (no idle sleep / display-off) while a paired client
/// is connected (see WindowsInteractiveSessionGuard). Off-by-default; non-Windows is unsupported.
/// (RemEx-l6o, RemEx-aep Phase 4)
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
