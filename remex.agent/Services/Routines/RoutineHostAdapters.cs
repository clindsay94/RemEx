using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Remex.Agent.Services.Security;
using Remex.Core.Services;
using Remex.Core.Services.Command;
using Remex.Core.Services.Network;

namespace Remex.Agent.Services.Routines;

// The seams between the routine step executor and the rest of the host (routines spec §8.4, §13.2).
// Each is one small interface over something that already exists, so RoutineStepExecutorTests can
// prove WHICH existing path a step takes (SharedCommandVerbs, the launcher allowlist, the media-key
// VKs, NotificationService) without a network hop, an elevated verb, or a real desktop.

/// <summary>Issues a routine power verb through the one shared verb table.</summary>
public interface IRoutinePowerExecutor
{
    /// <summary>
    /// Executes <paramref name="verb"/>. Returns null when the verb is not a shared one (never expected:
    /// the executor checks <c>RoutinePowerVerbs.IsAllowed</c> first).
    /// </summary>
    Task<SharedCommandVerbs.Outcome?> ExecuteAsync(string verb, int? delaySeconds);
}

/// <summary>
/// <see cref="IRoutinePowerExecutor"/> over <see cref="SharedCommandVerbs.TryExecuteAsync"/>: the SAME
/// implementation both command ingresses dispatch, so a routine's SHUTDOWN cannot drift from the
/// phone's power button (RemEx-pmb4). In-process only; nothing here touches TCP 8338 (T15).
/// </summary>
public sealed class SharedVerbRoutinePowerExecutor(ISystemCommandService commands, IWakeOnLanService wakeOnLan)
    : IRoutinePowerExecutor
{
    /// <inheritdoc />
    public Task<SharedCommandVerbs.Outcome?> ExecuteAsync(string verb, int? delaySeconds)
    {
        Dictionary<string, string>? parameters = null;
        if (delaySeconds is > 0)
        {
            parameters = new Dictionary<string, string>
            {
                ["DelaySeconds"] = delaySeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        return SharedCommandVerbs.TryExecuteAsync(verb, parameters, commands, wakeOnLan);
    }
}

/// <summary>Sends one media key the way the phone's media buttons do.</summary>
public interface IRoutineMediaKeys
{
    /// <summary>Presses and releases <paramref name="virtualKey"/>. False when no input backend can.</summary>
    bool TrySend(int virtualKey);
}

/// <summary>
/// <see cref="IRoutineMediaKeys"/> over <see cref="IInputSimulationService"/>: the Windows VKs
/// <c>0xB3</c>/<c>0xB0</c>/<c>0xB1</c>, translated on Linux by <c>LinuxInputEventTranslator</c>. This
/// is the path the Remote Control screen's media row already rides (<c>desktop_input</c> key events).
/// </summary>
public sealed class InputSimulationMediaKeys(
    IInputSimulationService input,
    IHostCapabilitiesProvider capabilities,
    ILogger<InputSimulationMediaKeys> logger) : IRoutineMediaKeys
{
    /// <inheritdoc />
    public bool TrySend(int virtualKey)
    {
        if (!capabilities.GetCurrent().SupportsInputSimulation)
        {
            return false;
        }

        try
        {
            input.KeyDown(virtualKey);
            input.KeyUp(virtualKey);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Routine media key 0x{Key:X2} could not be sent.", virtualKey);
            return false;
        }
    }
}

/// <summary>What the host knows about a routine's owner phone.</summary>
public interface IRoutineOwnerDirectory
{
    /// <summary>True while <paramref name="clientId"/> is still paired (T6 revalidation).</summary>
    bool IsPaired(string clientId);

    /// <summary>The phone's display name, or null when none is on file.</summary>
    string? DisplayName(string clientId);
}

/// <summary><see cref="IRoutineOwnerDirectory"/> over the pairing registry and the name store.</summary>
public sealed class PairedRoutineOwnerDirectory(
    PairedClientRegistry registry,
    PairedClientNameStore names,
    PairedDeviceNameOverrideStore overrides) : IRoutineOwnerDirectory
{
    /// <inheritdoc />
    public bool IsPaired(string clientId) => registry.IsClientPaired(clientId);

    /// <inheritdoc />
    public string? DisplayName(string clientId) =>
        overrides.Snapshot().TryGetValue(clientId, out var typed) && !string.IsNullOrWhiteSpace(typed)
            ? typed
            : names.Resolve(clientId);
}

/// <summary>Whether the interactive desktop is behind the lock screen right now.</summary>
public interface ISessionLockProbe
{
    /// <summary>
    /// True when the countdown window cannot be seen because the secure desktop is up. False when
    /// unlocked OR unknown: an unknown state must not suppress the window.
    /// </summary>
    bool IsLocked();
}

/// <summary>
/// <see cref="ISessionLockProbe"/>: on Windows the input desktop is <c>Default</c> while unlocked and
/// <c>Winlogon</c> (or unopenable) while locked or at a secure prompt. Linux reads the session source's
/// last state (logind <c>LockedHint</c>, or the ScreenSaver <c>ActiveChanged</c> fallback, routines S4);
/// with no session source it answers "unknown", which must not suppress the window.
/// </summary>
public sealed class SessionLockProbe(RoutineTriggerAvailability? availability = null) : ISessionLockProbe
{
    /// <inheritdoc />
    public bool IsLocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return availability?.SessionLocked ?? false;
        }

        try
        {
            return IsInputDesktopSecure();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsInputDesktopSecure()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            // The agent is elevated but not SYSTEM, so the Winlogon desktop refuses it: an input
            // desktop we cannot open is the secure desktop.
            return true;
        }

        try
        {
            var buffer = new char[64];
            if (!GetUserObjectInformation(desktop, UoiName, buffer, buffer.Length * sizeof(char), out _))
            {
                return false;
            }

            var name = new string(buffer).TrimEnd('\0');
            return !string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, [Out] char[] pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);
}
