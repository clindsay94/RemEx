namespace Remex.Desktop.Services;

/// <summary>When a settings change actually takes effect.</summary>
public enum SettingEffect
{
    /// <summary>Applies as soon as it is changed.</summary>
    Immediate,

    /// <summary>Stored now, but the running host keeps the old value until it restarts.</summary>
    AfterRestart
}

/// <summary>
/// Says which settings need a host restart before they do anything (RemEx-pbp4).
/// </summary>
/// <remarks>
/// <para>
/// There are zero hits repo-wide for restart-required today, so a setting that needs a restart looks
/// identical to one that applies live. **THE FAILURE IS SILENT AND UNDIAGNOSABLE FROM THE USER'S
/// SIDE:** they flip a switch, nothing changes, and there is no way to tell "this setting is broken"
/// from "this setting has not started yet". The point of tagging a row is that the user learns which
/// one they are looking at.
/// </para>
/// <para>
/// **RESTARTING IS NOT FREE HERE, AND THAT SHAPES THE UI RATHER THAN THIS TABLE.** The app IS the
/// host, so restarting it drops the connected phone's session mid-whatever-it-is-doing. A
/// "Restart RemEx now" action must confirm and must say that, rather than presenting itself as a
/// tidy-up.
/// </para>
/// </remarks>
public static class SettingRestartRequirement
{
    /// <summary>
    /// Settings the running host reads once at startup.
    /// </summary>
    /// <remarks>
    /// Keyed by a stable setting id rather than by a localized label, so translating the UI cannot
    /// silently empty this table.
    /// </remarks>
    private static readonly HashSet<string> NeedsRestart = new(StringComparer.Ordinal)
    {
        // Read when the listener is built. Changing it later leaves the old socket bound.
        "host.port",

        // The capture stack is constructed once; a live swap would have to tear down an active
        // stream, which is a bigger change than a picker.
        "capture.backend",

        // Applied by the autostart entry's arguments, which only matter at the next launch.
        "startup.startMinimized"
    };

    /// <summary>
    /// Settings that have been checked and confirmed to apply live.
    /// </summary>
    /// <remarks>
    /// THIS LIST IS WHY THE SAFE DEFAULT DOES NOT BECOME A PERMANENT LIE. Without it every setting
    /// nobody had classified would carry the chip forever, users would learn the chip means nothing,
    /// and it would stop working for the settings that genuinely need it. Adding a setting here is
    /// a statement that someone checked it applies immediately.
    /// </remarks>
    private static readonly HashSet<string> AppliesLive = new(StringComparer.Ordinal)
    {
        "appearance.theme",
        "appearance.accent",
        "appearance.fontFamily",
        "appearance.fontScale",
        "appearance.cornerRadius",
        "general.language",
        "general.closeToTray",
        "startup.launchAtLogin",
        "layout.homeCards",
        "logs.captureLevel"
    };

    /// <summary>
    /// When a change to <paramref name="settingId"/> takes effect.
    /// </summary>
    /// <remarks>
    /// **AN UNCLASSIFIED SETTING IS TREATED AS NEEDING A RESTART, AND THAT DEFAULT IS DELIBERATE.**
    /// The two ways to be wrong are not equal. Wrongly showing the chip costs the user a restart
    /// they did not need — visible, and annoying at worst. Wrongly omitting it leaves them staring
    /// at a setting that silently does nothing, which reads as a broken feature and cannot be told
    /// apart from one. So a setting nobody has classified errs toward telling the user something
    /// might be pending, rather than toward a confident silence.
    /// </remarks>
    public static SettingEffect EffectOf(string? settingId)
    {
        if (string.IsNullOrWhiteSpace(settingId)) return SettingEffect.AfterRestart;
        if (NeedsRestart.Contains(settingId)) return SettingEffect.AfterRestart;

        return AppliesLive.Contains(settingId)
            ? SettingEffect.Immediate
            : SettingEffect.AfterRestart;
    }

    /// <summary>
    /// Whether the row should carry a "takes effect after restart" chip.
    /// </summary>
    public static bool ShowsRestartChip(string? settingId) =>
        EffectOf(settingId) == SettingEffect.AfterRestart;

    /// <summary>Settings known to need a restart.</summary>
    public static IReadOnlySet<string> RestartRequiredSettings => NeedsRestart;

    /// <summary>Settings confirmed to apply live.</summary>
    public static IReadOnlySet<string> LiveApplyingSettings => AppliesLive;
}
