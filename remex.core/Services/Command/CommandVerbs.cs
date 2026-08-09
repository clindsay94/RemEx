namespace Remex.Core.Services.Command;

/// <summary>
/// Which command verbs each ingress accepts (RemEx-q7l0c).
/// </summary>
/// <remarks>
/// <para>
/// **THE TWO TABLES ARE MAINTAINED BY HAND IN TWO PROJECTS AND HAVE DRIFTED.** Answering "can the
/// TCP ingress kill a process?" required reading a switch in <c>RemexNetworkListener</c> and another
/// in <c>PingPongHandler</c> and diffing them by eye. This states both, so the answer is a list
/// rather than an inspection.
/// </para>
/// <para>
/// **THE SPLIT IS A SECURITY BOUNDARY, NOT AN OVERSIGHT.** TCP 8338 is external attack surface,
/// hardened in RemEx-s032.2, and it deliberately gets only whole-machine power actions — things a
/// script ingress can reasonably ask for and whose blast radius is the machine's power state. The
/// verbs it does NOT get are the ones that name a target: killing a chosen process, launching a
/// chosen executable, taking a screenshot of whatever is on screen. Those arrive over the paired,
/// authenticated <c>/ws</c> channel or not at all.
/// </para>
/// <para>
/// DEFAULT TO MINIMAL, which is RemEx-pmb4's recorded decision: this list describes what is
/// dispatched today and nothing more. Adding a verb to <see cref="ScriptIngress"/> widens an
/// external surface and wants a concrete documented need, not a symmetry argument.
/// </para>
/// <para>
/// A GUARD COMPARES EACH LIST TO ITS DISPATCHER'S ACTUAL SWITCH, because a list that drifts from the
/// code is worse than no list — it answers the question confidently and wrongly. That is exactly how
/// this got out of date: SCREENSHOT landed on <c>/ws</c> (RemEx-byij) and the written record of the
/// split did not follow it.
/// </para>
/// </remarks>
public static class CommandVerbs
{
    /// <summary>Verbs the TCP 8338 script ingress dispatches.</summary>
    /// <remarks>
    /// Whole-machine power actions only. Nothing here names a process, a path, or a screen.
    /// </remarks>
    public static readonly IReadOnlyList<string> ScriptIngress =
    [
        "SHUTDOWN",
        "FORCESHUTDOWN",
        "RESTART",
        "FORCERESTART",
        "RESTARTTOUEFI",
        "SLEEP",
        "HIBERNATE",
        "SIGNOUT",
        "LOCK",
        "MONITOROFF",
        "WAKEONLAN",
    ];

    /// <summary>Verbs the paired <c>/ws</c> channel dispatches.</summary>
    /// <remarks>
    /// Everything the script ingress gets, plus the four that name a target and therefore require an
    /// authenticated, paired peer: KILLPROCESS, KILLPROCESSELEVATED, LAUNCHAPP and SCREENSHOT.
    /// </remarks>
    public static readonly IReadOnlyList<string> PairedChannel =
    [
        .. ScriptIngress,
        "KILLPROCESS",
        "KILLPROCESSELEVATED",
        "LAUNCHAPP",
        "SCREENSHOT",
    ];

    /// <summary>Verbs the paired channel has that the script ingress deliberately does not.</summary>
    /// <remarks>
    /// Derived rather than written out, so it cannot disagree with the two lists above. This is the
    /// set whose growth should be argued for: each member is a verb that acts on something the caller
    /// chose.
    /// </remarks>
    public static IReadOnlyList<string> PairedOnly =>
        [.. PairedChannel.Except(ScriptIngress)];
}
