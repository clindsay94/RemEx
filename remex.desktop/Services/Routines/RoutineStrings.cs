using Remex.Core.Routines;

namespace Remex.Desktop.Services.Routines;

/// <summary>
/// The localized routine text the PC shows: action labels, source labels, and formatted messages.
/// </summary>
/// <remarks>
/// Formatting never throws. A translation that gains or loses a placeholder would otherwise raise
/// <see cref="FormatException"/> in one language only, and here that exception would land inside a
/// countdown or a failure notice, which are exactly the moments the user must not lose.
/// </remarks>
public static class RoutineStrings
{
    /// <summary>The localized label of a routine power verb ("Shut down"); the raw verb when unknown.</summary>
    public static string ActionLabel(string? verb) =>
        RoutinePowerVerbs.IsAllowed(verb)
            ? LocalizationService.Instance[$"Routine_Action_{verb}"]
            : verb ?? string.Empty;

    /// <summary>The localized label of a run source (§8.8); a generic label for anything else.</summary>
    public static string SourceLabel(string? source)
    {
        var key = source switch
        {
            RoutineRunSources.ManualApp => "Routine_Source_ManualApp",
            RoutineRunSources.ManualShortcut => "Routine_Source_ManualShortcut",
            RoutineRunSources.ManualWidget => "Routine_Source_ManualWidget",
            RoutineRunSources.NfcTap => "Routine_Source_NfcTap",
            RoutineRunSources.HomeArrive => "Routine_Source_HomeArrive",
            RoutineRunSources.HomeLeave => "Routine_Source_HomeLeave",
            _ => "Routine_Source_Other",
        };
        return LocalizationService.Instance[key];
    }

    /// <summary>Formats a localized string with the active culture, falling back to the bare text.</summary>
    public static string Format(string key, params object?[] args)
    {
        var loc = LocalizationService.Instance;
        var pattern = loc[key];
        try
        {
            return string.Format(loc.Culture, pattern, args);
        }
        catch (FormatException)
        {
            return pattern;
        }
    }
}
