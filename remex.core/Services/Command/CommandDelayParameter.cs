namespace Remex.Core.Services.Command;

/// <summary>
/// Reads the optional delay a power command may carry, from the command's parameter dictionary.
/// </summary>
/// <remarks>
/// This lived twice, byte for byte, in <c>PingPongHandler</c> and <c>RemexNetworkListener</c> — the
/// WebSocket and TCP ingress paths respectively. Two copies of a parser that decides how long before
/// a machine shuts down is the kind of duplication that stays in sync right up until it does not:
/// adding a fourth accepted key or changing the clamp in one path would silently give the two
/// transports different behaviour for the same command. (RemEx-xmgw.)
/// <para>
/// Kept in <c>Remex.Core</c> so both callers can reach it, and deliberately free of reflection and
/// non-source-generated serialization so it stays NativeAOT-safe — this assembly is also compiled
/// into <c>libRemexCore.so</c>.
/// </para>
/// </remarks>
public static class CommandDelayParameter
{
    /// <summary>
    /// The largest accepted delay, in seconds: ten years.
    /// </summary>
    /// <remarks>
    /// Not a meaningful scheduling horizon — it is an upper bound that keeps a hostile or garbled
    /// value from being handed to the platform's shutdown scheduler, while being far beyond any
    /// delay a person would set deliberately.
    /// </remarks>
    public const int MaxDelaySeconds = 315_360_000;

    /// <summary>
    /// Returns the delay in seconds, or 0 when none was supplied or the value was unusable.
    /// </summary>
    /// <remarks>
    /// Three key spellings are accepted because the clients have used all three over time; the first
    /// one that parses to a positive number wins. Anything absent, unparseable, zero or negative
    /// yields 0, meaning "act now" — the same answer as sending no delay at all, which is the safe
    /// reading of a malformed value for a command that is about to power down a machine.
    /// </remarks>
    public static int ParseDelaySeconds(Dictionary<string, string>? parameters)
    {
        if (parameters == null)
        {
            return 0;
        }

        foreach (var key in new[] { "DelaySeconds", "Seconds", "TimerSeconds" })
        {
            if (parameters.TryGetValue(key, out var raw)
                && int.TryParse(raw, out var parsed)
                && parsed > 0)
            {
                return Math.Clamp(parsed, 0, MaxDelaySeconds);
            }
        }

        return 0;
    }
}
