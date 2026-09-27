namespace Remex.Desktop.Services.Routines;

/// <summary>
/// Whether this process was started with <c>--routines-dry-run</c> (routines spec §8.6, §8.9, T23).
/// </summary>
/// <remarks>
/// <para>
/// <b>THE COMMAND LINE IS THE ONLY WAY IN.</b> Dry run turns the one safety a routine's power step
/// has - it actually happens, and history says so - into "logged, not carried out". A setting, a
/// file or a wire field that could flip it would let a paired phone, or anything that can write the
/// config directory, quietly defang a SHUTDOWN routine or muddy its history. So the flag is decided
/// once, from <c>argv</c>, when the host is built, and nothing can change it afterwards: there is no
/// setter. <c>RoutineDryRunSwitchTests</c> pins that shape.
/// </para>
/// <para>
/// It lives in the desktop assembly rather than the agent because the PC Routines page (S4) shows a
/// persistent "Dry run" banner from <see cref="IsEnabled"/>, and the UI cannot reference agent types.
/// The agent registers the one instance in its container; the UI reads it through
/// <c>App.EmbeddedHostServices</c>.
/// </para>
/// </remarks>
public sealed class RoutineDryRunMode
{
    /// <summary>The exact switch, matched case-insensitively as a whole argument.</summary>
    public const string CommandLineSwitch = "--routines-dry-run";

    /// <summary>The ordinary state: power steps are carried out.</summary>
    public static RoutineDryRunMode Off { get; } = new(false);

    private RoutineDryRunMode(bool enabled) => IsEnabled = enabled;

    /// <summary>True when destructive routine verbs are logged instead of issued.</summary>
    public bool IsEnabled { get; }

    /// <summary>Reads the switch from the process arguments. Null or empty means off.</summary>
    public static RoutineDryRunMode FromCommandLine(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return Off;
        }

        foreach (var arg in args)
        {
            if (string.Equals(arg?.Trim(), CommandLineSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return new RoutineDryRunMode(true);
            }
        }

        return Off;
    }
}
