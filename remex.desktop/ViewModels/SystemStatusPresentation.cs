using Remex.Core.Services.Readiness;

namespace Remex.Desktop.ViewModels;

/// <summary>What the card offers the user for a row that is not <see cref="ReadinessState.Ok"/>.</summary>
public enum SystemStatusAffordance
{
    /// <summary>Report the state and offer nothing. The row is still shown.</summary>
    ReportOnly,

    /// <summary>Perform a specific, named repair, only when the user asks for it.</summary>
    Fix,

    /// <summary>
    /// Explain what the state means and what to do about it, in a dialog (RemEx-tb0a).
    /// </summary>
    /// <remarks>
    /// Held back from RemEx-id37 because there was nowhere for it to go, and a button that does
    /// nothing teaches the user the whole card does nothing. The destination is an in-app dialog per
    /// check rather than a docs URL — Connor's call, and the right one: the firewall row is the check
    /// most likely to be why a phone cannot reach the PC, and a link is no use to someone whose
    /// network is the thing that is broken.
    /// </remarks>
    Explain,
}

/// <summary>
/// Turns a <see cref="ReadinessCheck"/> into the keys and affordance the card renders (RemEx-id37).
/// </summary>
/// <remarks>
/// <para>
/// **KEYS BY CONVENTION, NOT A LOOKUP TABLE.** Every sentence the card can show is
/// <c>SystemStatus_{Id}_{State}</c> and every row heading is <c>SystemStatus_{Id}_Title</c>. That is
/// worth more than the typing it saves: a table has to be kept in step with two enums by hand, and
/// nothing fails when it is not — a missing entry renders blank, which is exactly the silent gap this
/// card exists to remove. Because the keys are derived, a test can enumerate the enums and assert
/// every combination resolves in all nine languages.
/// </para>
/// <para>
/// **THIS TYPE NEVER TOUCHES <see cref="ReadinessCheck.Detail"/>, AND THAT IS THE POINT.** Detail is
/// developer-facing English assembled for logs, sometimes from an exception message. Routing user
/// text through keys means a user-facing string CANNOT be built from it by accident — there is no
/// code path from one to the other, rather than a rule someone has to remember.
/// </para>
/// </remarks>
public static class SystemStatusPresentation
{
    /// <summary>The localization key for a row's heading.</summary>
    public static string TitleKey(ReadinessCheckId id) => $"SystemStatus_{id}_Title";

    /// <summary>The localization key for what this row says in this state.</summary>
    public static string SentenceKey(ReadinessCheckId id, ReadinessState state) =>
        $"SystemStatus_{id}_{state}";

    /// <summary>
    /// What the card may offer for this check, before considering its state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **THE CERTIFICATE ROW REPORTS AND NOTHING ELSE.** The only repair anyone would write for a
    /// broken certificate is to regenerate it — and that invalidates the SPKI hash every paired phone
    /// pinned, so one button would un-pair every device at once with no way back except re-pairing
    /// each by hand. CLAUDE.md classes certificate and ACL handling as high-risk for this reason.
    /// Reporting the problem is genuinely useful; offering to "fix" it is not.
    /// </para>
    /// <para>
    /// AUTOSTART IS THE ONE ROW WITH A BUTTON, because it is the one place a repair already exists
    /// that is local, reversible and entirely within RemEx's own configuration: registering the logon
    /// task. Elevation, the listening port and the firewall would each mean changing something
    /// outside RemEx, and this card is not the place to do that.
    /// </para>
    /// <para>
    /// **AND THE REST GET NO "EXPLAIN" BUTTON, WHICH IS A DEVIATION FROM THE BEAD WORTH STATING.**
    /// The bead asked for a Fix or Explain button on every amber row. There is nowhere for Explain to
    /// go — no help page, no dialog — so the button would have been inert, and this repo's standards
    /// forbid placeholder implementations for good reason: a button that does nothing teaches the
    /// user that the card does nothing. The sentence beside each row already IS the explanation, and
    /// it says what the state means for them. A real help destination is filed separately.
    /// </para>
    /// </remarks>
    public static SystemStatusAffordance AffordanceFor(ReadinessCheckId id) => id switch
    {
        // Fix, not Explain: autostart is the one check with a local, reversible repair the user can
        // ask for. Everything else needs a person to change something outside RemEx, so the honest
        // offer is an explanation rather than a button that pretends to act.
        ReadinessCheckId.Autostart => SystemStatusAffordance.Fix,
        _ => SystemStatusAffordance.Explain,
    };

    /// <summary>Resource key for the "what this means and what to do" text of a check (RemEx-tb0a).</summary>
    /// <remarks>
    /// Derived from the enum name rather than switched, so a new <see cref="ReadinessCheckId"/> gets a
    /// key by construction. The missing-resource case is what makes that safe to do: a key with no
    /// entry surfaces as the key itself, which is visible immediately, where a switch would need a
    /// default arm that silently showed some other check's advice.
    /// </remarks>
    public static string HelpBodyKeyFor(ReadinessCheckId id) => $"SystemStatus_Help_{id}";

    /// <summary>
    /// Whether this row shows its affordance at all.
    /// </summary>
    /// <remarks>
    /// An <see cref="ReadinessState.Ok"/> row has nothing to fix or explain, so offering a button
    /// there would invite the user to act on something that is already fine. Note this deliberately
    /// says "not Ok" rather than "is a Problem": Warning and Unknown both mean something is worth
    /// their attention, which is the same reason <see cref="SystemReadinessReport.IsFullyReady"/>
    /// refuses to treat them as passing.
    /// </remarks>
    public static bool ShowsAffordance(ReadinessCheck check) =>
        check.State != ReadinessState.Ok
        && AffordanceFor(check.Id) != SystemStatusAffordance.ReportOnly;

    /// <summary>Rows worst-first, so the thing most likely to be blocking the user is read first.</summary>
    /// <remarks>
    /// Ordered by the SAME ranking <see cref="SystemReadinessReport.Overall"/> uses. If the card
    /// sorted by anything else, its headline state and its top row could disagree about what is
    /// worst — and the user would have no way to tell which to believe.
    /// </remarks>
    public static IReadOnlyList<ReadinessCheck> WorstFirst(IReadOnlyList<ReadinessCheck> checks) =>
        [.. checks
            .OrderByDescending(c => SystemReadinessReport.Severity(c.State))
            .ThenBy(c => c.Id)];
}
