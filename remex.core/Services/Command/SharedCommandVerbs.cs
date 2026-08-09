using Remex.Core.Services.Network;

namespace Remex.Core.Services.Command;

/// <summary>
/// The one implementation of the verbs BOTH ingresses dispatch (RemEx-pmb4).
/// </summary>
/// <remarks>
/// <para>
/// **THE TWO TABLES WERE MAINTAINED BY HAND IN TWO PROJECTS AND HAD DRIFTED.** <c>CommandVerbs</c>
/// (RemEx-q7l0c) made the split explicit and added a guard that fails when a declared list stops
/// matching its dispatcher — but a guard only DETECTS drift. This removes the second copy, so a verb
/// cannot half-exist in the first place: change the behaviour of SHUTDOWN here and both the TCP 8338
/// script ingress and the paired <c>/ws</c> channel change together, because there is only one of it.
/// </para>
/// <para>
/// **THE SPLIT ITSELF IS NOT WEAKENED BY SHARING THE IMPLEMENTATION.** Which channel may ask for
/// which verb is still <c>CommandVerbs.ScriptIngress</c> versus <c>CommandVerbs.PairedChannel</c>,
/// and the four target-naming verbs — KILLPROCESS, KILLPROCESSELEVATED, LAUNCHAPP, SCREENSHOT — are
/// deliberately absent from here. They need per-connection state (a process list, a launcher
/// allowlist, a screen) that this static table has no business holding, and keeping them out means
/// nothing here can accidentally widen the external surface hardened in RemEx-s032.2.
/// </para>
/// <para>
/// **NO EXPANSION OF 8338, WHICH IS A RECORDED DECISION AND NOT AN OVERSIGHT.** Connor lifted the
/// sign-off gate on 2026-08-01 and left the default-to-minimal recommendation standing: 8338 is
/// external attack surface, and this refactor found no concrete documented need to widen it. The
/// verb set is exactly what it dispatched before.
/// </para>
/// <para>
/// **THE WORDING IS THE /ws CHANNEL'S, NOT 8338's, AND THE DIRECTION MATTERS.** Android renders
/// <c>CommandMessage</c> verbatim inside "Success: %1$s", so "Shutdown executed." reads correctly
/// both standalone and framed, while 8338's older "Shutdown command executed successfully." only
/// reads correctly standalone — framed it becomes "Success: Shutdown command executed
/// successfully." Unifying on the phrasing that survives both contexts costs 8338 nothing: its
/// callers are scripts that branch on the boolean.
/// </para>
/// <para>
/// NativeAOT-safe by construction: a switch over string literals, no reflection, no dynamic
/// dispatch. It returns an outcome rather than a transport type, because the two ingresses
/// have different envelopes — 8338 answers with <c>CommandResponse</c>, <c>/ws</c> with a
/// <c>RemexMessage</c> — and the point is to share the BEHAVIOUR, not to force one wire format on
/// the other.
/// </para>
/// </remarks>
public static class SharedCommandVerbs
{
    /// <summary>What a shared verb did, in terms both ingresses can phrase for their own envelope.</summary>
    public readonly record struct Outcome(bool Success, string Message, string? ErrorDetails = null);

    /// <summary>
    /// Executes <paramref name="verb"/> if it is one both ingresses share.
    /// </summary>
    /// <returns>
    /// The outcome, or <c>null</c> when the verb is not a shared one — which is the caller's signal
    /// to try its own channel-specific verbs, or to refuse.
    /// </returns>
    /// <remarks>
    /// Returning null rather than throwing keeps the "unknown verb" answer where it belongs: with
    /// the ingress, which knows what its own channel additionally accepts and how it words a refusal.
    /// </remarks>
    public static async Task<Outcome?> TryExecuteAsync(
        string verb,
        // The CONCRETE dictionary, not IReadOnlyDictionary, because that is what both ingresses
        // already hold and what CommandDelayParameter already takes. Widening the parameter here
        // would only move a conversion to two call sites to look tidier in one signature.
        Dictionary<string, string>? parameters,
        ISystemCommandService commands,
        IWakeOnLanService wakeOnLan)
    {
        // Delay is parsed identically for the five verbs that accept one, through the same helper
        // both ingresses already used - keeping that shared is half the point of this type.
        var delay = CommandDelayParameter.ParseDelaySeconds(parameters);

        switch (verb)
        {
            case "SHUTDOWN":
                await commands.Shutdown(delay);
                return new Outcome(true, "Shutdown executed.");
            case "FORCESHUTDOWN":
                await commands.ForceShutdown(delay);
                return new Outcome(true, "Force shutdown executed.");
            case "RESTART":
                await commands.Restart(delay);
                return new Outcome(true, "Restart executed.");
            case "FORCERESTART":
                await commands.ForceRestart(delay);
                return new Outcome(true, "Force restart executed.");
            case "RESTARTTOUEFI":
                await commands.RestartToUefi(delay);
                return new Outcome(true, "Restart to UEFI executed.");
            case "SLEEP":
                await commands.Sleep();
                return new Outcome(true, "Sleep executed.");
            case "HIBERNATE":
                await commands.Hibernate();
                return new Outcome(true, "Hibernate executed.");
            case "SIGNOUT":
                await commands.SignOut();
                return new Outcome(true, "Sign out executed.");
            case "LOCK":
                await commands.Lock();
                return new Outcome(true, "Lock executed.");
            case "MONITOROFF":
                await commands.MonitorOff();
                return new Outcome(true, "Monitor off executed.");

            case "WAKEONLAN":
                // The only shared verb with a required parameter, and the only one that can fail
                // without the service being involved at all.
                if (parameters is null || !parameters.TryGetValue("MacAddress", out var mac))
                {
                    return new Outcome(false, "Missing MacAddress parameter.", "Wake-on-LAN requires a MacAddress parameter.");
                }

                var broadcast = parameters.TryGetValue("BroadcastIp", out var bip) ? bip : "255.255.255.255";
                var port = parameters.TryGetValue("Port", out var portText) && int.TryParse(portText, out var parsed)
                    ? parsed
                    : 9;

                await wakeOnLan.WakeAsync(mac, broadcast, port);
                return new Outcome(true, $"Wake-on-LAN packet sent to {mac}.");

            default:
                return null;
        }
    }
}
