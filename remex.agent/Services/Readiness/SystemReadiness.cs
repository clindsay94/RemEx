namespace Remex.Agent.Services.Readiness;

/// <summary>
/// How a single readiness check came out.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Unknown"/> IS THE WHOLE REASON THIS IS NOT A BOOLEAN, and it must never be presented
/// as if it were <see cref="Ok"/>. The target user may not be technical (CLAUDE.md, User Experience
/// Standards), and a card that renders "I could not check" as green tells them their machine is
/// ready when nobody established that. The support case this feature exists to prevent — "my phone
/// can't find my PC" starting from zero information — gets actively WORSE if the card has already
/// assured them everything is fine, because it removes the first thing they would otherwise have
/// looked at.
/// </para>
/// <para>
/// So the aggregate deliberately does not collapse to green while any row is unknown. See
/// <see cref="SystemReadinessReport.Overall"/>.
/// </para>
/// </remarks>
public enum ReadinessState
{
    /// <summary>Checked, and correct.</summary>
    Ok,

    /// <summary>Checked, and works today but will not survive something ordinary — usually a reboot.</summary>
    Warning,

    /// <summary>Checked, and broken now.</summary>
    Problem,

    /// <summary>NOT checked. Never render this as <see cref="Ok"/>.</summary>
    Unknown,

    /// <summary>
    /// Does not apply to this operating system, and so is not a row at all.
    /// </summary>
    /// <remarks>
    /// DISTINCT FROM <see cref="Unknown"/> ON PURPOSE. Elevation is the case that forced it: on
    /// Windows an elevated token is load-bearing, while on Linux the agent is a normal user process
    /// and there is nothing to elevate. Reporting that as "could not check" would leave the card
    /// permanently amber on every Linux machine and train the user to ignore it, which is a worse
    /// outcome than not asking. Cross-platform parity per CLAUDE.md means an honest per-OS "not
    /// checkable" state, not a fabricated pass.
    /// </remarks>
    NotApplicable
}

/// <summary>
/// Which check a row reports on. The UI maps this to a localized label; the enum never reaches a user.
/// </summary>
public enum ReadinessCheckId
{
    /// <summary>The process is running elevated. Load-bearing: a medium-integrity start bricks pairings.</summary>
    Elevation,

    /// <summary>The machine-wide certificate exists and can be read.</summary>
    Certificate,

    /// <summary>Something is listening on the port the phone connects to.</summary>
    PortListening,

    /// <summary>The logon task / XDG autostart entry is actually registered.</summary>
    Autostart
}

/// <summary>
/// One row of the readiness card.
/// </summary>
/// <param name="Id">Which check this is.</param>
/// <param name="State">How it came out.</param>
/// <param name="Detail">
/// Developer-facing detail for logs and diagnostics — NOT shown to the user, and therefore not
/// localized (CLAUDE.md: internal strings may stay English). The card renders a localized sentence
/// chosen from <paramref name="Id"/> and <paramref name="State"/>, so that a user-facing string can
/// never be accidentally assembled from an exception message.
/// </param>
public sealed record ReadinessCheck(ReadinessCheckId Id, ReadinessState State, string Detail);

/// <summary>
/// The full readiness picture.
/// </summary>
public sealed record SystemReadinessReport(IReadOnlyList<ReadinessCheck> Checks)
{
    /// <summary>
    /// The worst state among the rows, which is what decides whether the card collapses.
    /// </summary>
    /// <remarks>
    /// ORDER IS NOT THE ENUM'S DECLARATION ORDER, and that is the point. Ok is declared first so it
    /// is the type's default value, which puts Unknown fourth and NotApplicable last — but Unknown
    /// ranks ABOVE Ok in severity here, because a report containing an unchecked row has not
    /// established that the machine is ready and must not collapse to green. Ranking by the enum's
    /// numeric value — the obvious <c>Checks.Max(c =&gt; c.State)</c> — would rank Unknown above
    /// Problem, and NotApplicable above both, and would do so silently.
    /// </remarks>
    public ReadinessState Overall =>
        Applicable.Count == 0
            ? ReadinessState.Unknown
            : Applicable.Select(c => c.State).OrderByDescending(Severity).First();

    /// <summary>
    /// The rows that mean something on this operating system — i.e. everything the card renders.
    /// </summary>
    public IReadOnlyList<ReadinessCheck> Applicable =>
        Checks.Where(c => c.State != ReadinessState.NotApplicable).ToList();

    /// <summary>
    /// True only when every row was checked AND every row is correct.
    /// </summary>
    /// <remarks>
    /// The card is collapsed on this, so it is the one place a bug would hide the whole feature.
    /// Written as "everything is Ok" rather than "nothing is a Problem" deliberately: the latter
    /// treats Warning and Unknown as passing.
    /// </remarks>
    public bool IsFullyReady =>
        Applicable.Count > 0 && Applicable.All(c => c.State == ReadinessState.Ok);

    /// <summary>
    /// Severity rank. Higher is worse. Unknown outranks Ok and Warning but not Problem: something
    /// we could not check is more alarming than something that merely will not survive a reboot,
    /// and less alarming than something we know is broken right now.
    /// </summary>
    internal static int Severity(ReadinessState state) => state switch
    {
        ReadinessState.NotApplicable => -1,
        ReadinessState.Ok => 0,
        ReadinessState.Warning => 1,
        ReadinessState.Unknown => 2,
        ReadinessState.Problem => 3,

        // An unrecognised state ranks as Unknown rather than Ok. A `default` that fell through to
        // green is how a future state would silently start collapsing the card.
        _ => 2
    };
}
