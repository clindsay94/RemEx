namespace Remex.Core.Models;

/// <summary>
/// Machine-readable reasons a file operation was refused (RemEx-6vd8).
/// </summary>
/// <remarks>
/// <para>
/// **THESE EXIST BECAUSE A CLIENT CANNOT BRANCH ON PROSE.** The host reports failures as
/// <c>errorMessage</c>, which today reads "A file named 'report.pdf' already exists." — English,
/// written for a human, and the only signal a phone has. String-matching it breaks the moment the
/// wording improves and cannot work at all once the host is localized, which is a stated goal.
/// So the one case the client must ACT on differently gets a code.
/// </para>
/// <para>
/// ADDITIVE AND OPTIONAL, so no <c>protocolVersion</c> bump: an older client ignores the field and
/// keeps showing the prose, and a newer client against an older host sees null and does the same.
/// The free-text message is never replaced — it stays the thing a person reads.
/// </para>
/// <para>
/// **THE LIST IS SHORT ON PURPOSE.** A code with no sender is worse than no code: RemEx-mneb is a
/// live example in this repo of a message type that was declared and round-trip tested while no
/// code path ever set it, leaving a handler that could not fire. Every constant here is emitted by
/// <c>FileTransferService</c> and asserted by a test; do not add one speculatively.
/// </para>
/// </remarks>
public static class FileTransferErrorCodes
{
    /// <summary>
    /// The destination name is taken, and the caller did not say what to do about it.
    /// </summary>
    /// <remarks>
    /// The one code the UI branches on: it is the difference between "something went wrong" and a
    /// question the user can answer — replace, keep both, or skip.
    /// </remarks>
    public const string DestinationExists = "destination_exists";

    /// <summary>
    /// The destination name is taken BY A FOLDER, and the source is a file (or vice versa).
    /// </summary>
    /// <remarks>
    /// SEPARATE FROM <see cref="DestinationExists"/> BECAUSE THE ANSWERS DIFFER. "Keep both" is
    /// still valid here, but "replace" would mean deleting a whole directory tree to make room for
    /// one file — an outcome nobody intends from a copy, and one no undo exists for. A client that
    /// treated the two codes alike would offer that button.
    /// </remarks>
    public const string DestinationIsDifferentKind = "destination_is_different_kind";

    /// <summary>
    /// "Keep both" produced a name the destination will not accept (RemEx-cirk).
    /// </summary>
    /// <remarks>
    /// **DISTINCT BECAUSE IT ARRIVES AFTER THE USER ALREADY ANSWERED.** The suffix lengthens the
    /// name, so one at the 255-character component limit comes back at 259 and cannot be created —
    /// measured, and note that Windows long-path support does not help, because the limit breached
    /// is the COMPONENT limit rather than the path limit. Reporting it as a collision would re-open
    /// the sheet on a question the user has answered, and the only answer that could work is the one
    /// they declined.
    /// <para>
    /// <c>NextAvailableName</c> guarantees the name is ABSENT from the destination, never that it is
    /// CREATABLE. A clamp inside it would have to guess rules it deliberately refuses to guess —
    /// ext4 counts 255 BYTES rather than characters, exFAT and CIFS differ again — so the create
    /// path handles the failure instead of pretending it cannot happen.
    /// </para>
    /// </remarks>
    public const string ResolvedNameUnusable = "resolved_name_unusable";

    /// <summary>
    /// The name the host was about to create was claimed by something else first (RemEx-od7s).
    /// </summary>
    /// <remarks>
    /// **SEPARATE FROM <see cref="ResolvedNameUnusable"/> BECAUSE ASKING AGAIN WORKS.** That name is
    /// creatable; the host simply lost a race for it — another paired device, a second message on
    /// the same socket, or the person at the PC saving a file. Retrying "keep both" re-lists the
    /// directory and picks the next free name, so this is a question the user can still answer.
    /// <para>
    /// ALSO EMITTED WHERE KEEP-BOTH NEVER RAN (RemEx-f448): a child inside a folder copy that
    /// collided with something arriving after the folder was created. The name reported is the
    /// child's, and the answer is the same — ask again and the folder takes a fresh name. Where
    /// asking again cannot help, the client's round cap ends it as an ordinary failure.
    /// </para>
    /// <para>
    /// **AND SEPARATE FROM <see cref="DestinationExists"/>, WHICH WOULD BE A DATA-LOSS BUTTON.** The
    /// client offers Replace for that code, and Replace re-answers the ORIGINAL request — overwrite
    /// the destination the user first named — while the sheet is showing the invented sibling. A
    /// user who chose "keep both" precisely to preserve the original would destroy it by answering
    /// a question about a different file. This code exists so "taken" can be said without offering
    /// that button.
    /// </para>
    /// <para>
    /// ADDITIVE, SO NO <c>protocolVersion</c> BUMP, and safe against every phone already installed:
    /// <c>FileConflictPolicy.actionsFor</c> sends any code it does not recognise to Skip-only, so an
    /// older client degrades to the previous behaviour instead of misreading this one.
    /// </para>
    /// </remarks>
    public const string ResolvedNameTaken = "resolved_name_taken";
}

/// <summary>
/// What the caller wants done when the destination name is already taken (RemEx-6vd8).
/// </summary>
/// <remarks>
/// Sent on the manage request so the answer arrives WITH the retry rather than as a separate
/// round trip that could race another client writing the same name.
/// </remarks>
public static class FileConflictResolutions
{
    /// <summary>
    /// Pick the next free "name (n).ext" and use that, reporting which name was used.
    /// </summary>
    /// <remarks>
    /// **THE HOST CHOOSES THE NAME, NEVER THE CLIENT.** If the phone composed "report (2).pdf"
    /// itself, two things go wrong: the host may pick differently and the user is shown one
    /// filename while getting another, or the guess collides too and the operation fails again with
    /// the same opaque error the whole feature exists to replace. The host also knows whether its
    /// own filesystem is case-sensitive, which the phone does not.
    /// </remarks>
    public const string KeepBoth = "keep_both";

    /// <summary>Overwrite the existing destination.</summary>
    /// <remarks>
    /// Equivalent to the existing <c>overwrite</c> flag and accepted here so a client can express
    /// all three answers through one field rather than two that could disagree.
    /// </remarks>
    public const string Replace = "replace";
}
