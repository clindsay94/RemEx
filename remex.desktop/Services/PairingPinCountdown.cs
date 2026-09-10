namespace Remex.Desktop.Services;

/// <summary>How much life a displayed pairing PIN has left.</summary>
public enum PairingPinState
{
    /// <summary>Usable, with comfortable time to type it.</summary>
    Valid,

    /// <summary>Still usable, but likely to expire while the user is typing it.</summary>
    ExpiringSoon,

    /// <summary>Dead. Must not be presented as enterable.</summary>
    Expired
}

/// <summary>The state of a displayed PIN at a moment in time.</summary>
/// <param name="State">Whether the PIN is usable.</param>
/// <param name="Remaining">
/// Time left, never negative. Zero exactly when <paramref name="State"/> is
/// <see cref="PairingPinState.Expired"/>.
/// </param>
public readonly record struct PairingPinStatus(PairingPinState State, TimeSpan Remaining);

/// <summary>
/// Works out whether a displayed pairing PIN is still worth typing (RemEx-scwy).
/// </summary>
/// <remarks>
/// <para>
/// Pairing is the PC's most important job, and expiry is currently a muted 11px line refreshed by a
/// one-second timer. **THE FAILURE THIS PREVENTS IS A PIN PRESENTED AS VALID AFTER IT IS DEAD.** A
/// user who types it gets a rejection with no explanation of the real cause, on the one flow where
/// a confusing failure sends them to support — and the PIN is screen-only, so there is nothing to
/// re-read afterwards.
/// </para>
/// <para>
/// Separated from the view because the arithmetic has edge cases a timer tick does not make
/// obvious, and because "is it dead yet" is not something to decide inside a
/// <c>DispatcherTimer</c> callback.
/// </para>
/// </remarks>
public static class PairingPinCountdown
{
    /// <summary>
    /// How long before expiry the UI should start warning.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds, chosen from the task rather than from taste: the user has to read six
    /// digits off one screen and type them into another, on a phone, possibly one-handed. A warning
    /// that arrives with three seconds left tells them something they can no longer act on.
    /// </remarks>
    public static readonly TimeSpan ExpiryWarningThreshold = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Computes the current status of a PIN.
    /// </summary>
    /// <param name="issuedAt">When the host issued the PIN.</param>
    /// <param name="validFor">How long the host will accept it.</param>
    /// <param name="now">
    /// The current time. Pass a MONOTONIC reading where one is available: a wall clock can step
    /// backwards on an NTP sync, and a PIN that appears to gain life is worse than one that loses
    /// it, because the user acts on the extra time.
    /// </param>
    public static PairingPinStatus Evaluate(DateTimeOffset issuedAt, TimeSpan validFor, DateTimeOffset now)
    {
        // A NON-POSITIVE WINDOW IS EXPIRED, NOT INFINITE. Treating it as "no limit" would present a
        // PIN the host will refuse, and a zero or negative validity almost certainly means the
        // caller got it from a field that was never populated.
        if (validFor <= TimeSpan.Zero) return new PairingPinStatus(PairingPinState.Expired, TimeSpan.Zero);

        var elapsed = now - issuedAt;

        // Clock stepped backwards, or the PIN is dated slightly in the future. Clamp to the FULL
        // window rather than trusting a negative elapsed: the alternative computes a remaining time
        // LONGER than the validity window, which is the one direction that must never happen.
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        var remaining = validFor - elapsed;

        if (remaining <= TimeSpan.Zero) return new PairingPinStatus(PairingPinState.Expired, TimeSpan.Zero);

        var state = remaining <= ExpiryWarningThreshold
            ? PairingPinState.ExpiringSoon
            : PairingPinState.Valid;

        return new PairingPinStatus(state, remaining);
    }

    /// <summary>
    /// Whether the PIN should still be shown to the user at all.
    /// </summary>
    /// <remarks>
    /// FALSE MEANS REPLACE IT, NOT GREY IT OUT. An expired PIN rendered faintly is still six digits
    /// on a screen, and a user will type them — the visual treatment carries no information to
    /// someone who is looking at their phone. The surface should show a "get a new PIN" action
    /// instead, which is the only thing that can actually help them.
    /// </remarks>
    public static bool ShouldDisplayPin(PairingPinState state) => state != PairingPinState.Expired;
}
