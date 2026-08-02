namespace Remex.Desktop.Services;

/// <summary>Where a transient notification should be shown, if anywhere.</summary>
public enum NotificationChannel
{
    /// <summary>An in-app toast. Only useful when the user can see the window.</summary>
    InApp,

    /// <summary>A tray balloon. The only channel that reaches a user whose window is hidden.</summary>
    TrayBalloon,

    /// <summary>Nothing transient — the event is recorded but not announced.</summary>
    Suppressed
}

/// <summary>How much the user needs to be told.</summary>
public enum NotificationImportance
{
    /// <summary>Progress and chatter. Worth a toast if they are looking; never worth interrupting.</summary>
    Informational,

    /// <summary>A completed action the user asked for — a file arrived, an export finished.</summary>
    Outcome,

    /// <summary>Something failed, or needs a decision.</summary>
    Problem
}

/// <summary>
/// Decides which notification channel an event should use (RemEx-1fxt).
/// </summary>
/// <remarks>
/// <para>
/// **CLOSE-TO-TRAY DEFAULTS ON, WHICH INVERTS THE USUAL ASSUMPTION.** Most events happen while the
/// window is hidden, so an in-app toast is not a reasonable default that occasionally goes unseen —
/// it is a channel that is invisible most of the time. A received file currently produces no
/// visible feedback at all, and routing it to a toast alone would leave that true.
/// </para>
/// <para>
/// The rules are separated from the presentation because the presentation is Avalonia and eight
/// languages, while the DECISION is the part that is wrong in a way a user notices.
/// </para>
/// </remarks>
public static class NotificationRouter
{
    /// <summary>
    /// Chooses the channel for an event.
    /// </summary>
    /// <param name="importance">How much the user needs to be told.</param>
    /// <param name="windowVisible">
    /// Whether the main window is on screen AND not minimized. A window behind other windows still
    /// counts as visible: the user chose to leave it open, and a balloon for something they could
    /// see by switching windows is the double-notification this exists to avoid.
    /// </param>
    public static NotificationChannel Route(NotificationImportance importance, bool windowVisible)
    {
        // A PROBLEM REACHES THE USER WHEREVER THEY ARE. This is the one importance that may
        // interrupt, because the alternative is the state this bead exists to fix: an error that
        // happened while minimized and was never seen by anyone.
        if (importance == NotificationImportance.Problem)
        {
            return windowVisible ? NotificationChannel.InApp : NotificationChannel.TrayBalloon;
        }

        // An outcome is something the user ASKED for, so they are waiting for it - a file they
        // accepted, an export they started. Hidden window means a balloon; otherwise a toast.
        if (importance == NotificationImportance.Outcome)
        {
            return windowVisible ? NotificationChannel.InApp : NotificationChannel.TrayBalloon;
        }

        // INFORMATIONAL NEVER BALLOONS, and this is the rule that keeps the feature tolerable.
        // Progress chatter that pops a balloon every time a transfer advances trains the user to
        // dismiss balloons without reading them - at which point the Problem case above stops
        // working too, because it arrives in a channel they have learned to ignore.
        return windowVisible ? NotificationChannel.InApp : NotificationChannel.Suppressed;
    }

    /// <summary>
    /// Whether an event should ALSO be written to the in-app log regardless of channel.
    /// </summary>
    /// <remarks>
    /// A suppressed notification must still leave a trace, or the diagnostics export cannot explain
    /// what happened while the window was hidden — which is precisely the window in which most
    /// things happen. Suppressing the announcement is a UI decision; suppressing the RECORD would
    /// repeat the swallowed-errors defect fixed in RemEx-43ha.
    /// </remarks>
    public static bool ShouldLog(NotificationImportance importance) =>
        importance != NotificationImportance.Informational;
}
