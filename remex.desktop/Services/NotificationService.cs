using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;

namespace Remex.Desktop.Services;

/// <summary>What actually happened to a notification, which is not the same as where it was routed.</summary>
/// <remarks>
/// <see cref="NotificationChannel"/> is the DECISION — it says where an event belongs. This says
/// where it ended up. The two differ whenever the chosen surface is not there to receive it, and
/// keeping one enum for both would make "we decided on a balloon" and "the user saw a balloon"
/// indistinguishable to every caller and every test.
/// </remarks>
public enum NotificationDelivery
{
    /// <summary>Shown as an in-app toast.</summary>
    InApp,

    /// <summary>Shown as a tray balloon.</summary>
    TrayBalloon,

    /// <summary>Deliberately not announced — the router said this event does not interrupt.</summary>
    Suppressed,

    /// <summary>The chosen surface was unavailable or refused. Announced nowhere; see the remarks on
    /// <see cref="NotificationService.Present"/> for why that is not the same as lost.</summary>
    Failed
}

/// <summary>The in-app toast surface, installed by the shell once it has a window to draw in.</summary>
public interface IInAppNotificationSink
{
    /// <summary>Shows a toast over the main window. Called on the UI thread.</summary>
    void Show(NotificationImportance importance, string title, string message);
}

/// <summary>The tray balloon surface — the only channel that reaches a user whose window is hidden.</summary>
public interface ITrayBalloonSink
{
    /// <summary>
    /// Shows a balloon near the tray. Called on the UI thread. Returns <see langword="false"/> when
    /// the balloon could not be shown, so the caller can record that rather than assume it landed.
    /// </summary>
    bool TryShow(NotificationImportance importance, string title, string message);
}

/// <summary>
/// Announces an event on whichever surface <see cref="NotificationRouter"/> chooses. A static
/// singleton, mirroring <see cref="ActivityService"/>, so call sites on both sides of the
/// host→desktop bridge can announce without threading a dependency through constructors.
/// </summary>
/// <remarks>
/// <para>
/// The surfaces are set from the outside — the shell installs the toast host once it has a window,
/// and <c>App</c> installs the balloon presenter and the visibility probe. That keeps the routing
/// and the fallback decisions testable without an Avalonia window, which is the whole reason
/// <see cref="Present"/> takes <c>windowVisible</c> rather than reading it.
/// </para>
/// <para>
/// <b>NEITHER SURFACE IS THE RECORD.</b> Every call site already leaves a durable trace of its own
/// before it announces — a received file is written to the activity feed, a swallowed exception to
/// the in-app log the diagnostics export reads. That separation is deliberate and is the
/// RemEx-43ha lesson: suppressing the ANNOUNCEMENT is a UI decision, suppressing the RECORD is a
/// defect. It is also what makes <see cref="NotificationDelivery.Failed"/> survivable rather than
/// silent.
/// </para>
/// </remarks>
public sealed class NotificationService
{
    private static readonly Lazy<NotificationService> _instance = new(() => new NotificationService());

    /// <summary>The process-wide instance used by call sites.</summary>
    public static NotificationService Instance => _instance.Value;

    /// <summary>
    /// Reports whether the main window is on screen and not minimized. Installed by <c>App</c>.
    /// </summary>
    public Func<bool>? WindowVisibleProbe { get; set; }

    /// <summary>The in-app toast surface, or <see langword="null"/> before the shell has loaded.</summary>
    public IInAppNotificationSink? InApp { get; set; }

    /// <summary>The tray balloon surface, or <see langword="null"/> before <c>App</c> has installed it.</summary>
    public ITrayBalloonSink? TrayBalloon { get; set; }

    /// <summary>
    /// How work reaches the UI thread. Replaceable so the routing can be tested without a
    /// dispatcher; the lambda is not evaluated at construction, so a test never touches
    /// <see cref="Dispatcher.UIThread"/> by merely creating one of these.
    /// </summary>
    public Action<Action> Dispatch { get; set; } = static work => Dispatcher.UIThread.Post(work);

    /// <summary>
    /// Announces an event from any thread. Both the visibility probe and the surfaces are Avalonia
    /// state, so everything after this line happens on the UI thread.
    /// </summary>
    public void Notify(NotificationImportance importance, string title, string message) =>
        Dispatch(() => Present(importance, title, message, IsWindowVisible()));

    /// <summary>
    /// Routes and shows one event. Call on the UI thread; prefer <see cref="Notify"/> elsewhere.
    /// </summary>
    /// <remarks>
    /// A <see cref="NotificationDelivery.Failed"/> result means no surface would take it. It is
    /// logged rather than swallowed, because a notification that quietly announced itself to nobody
    /// looks exactly like one that was never raised, and the in-app log is what the diagnostics
    /// export reads.
    /// </remarks>
    public NotificationDelivery Present(
        NotificationImportance importance, string title, string message, bool windowVisible)
    {
        switch (NotificationRouter.Route(importance, windowVisible))
        {
            case NotificationChannel.InApp:
                if (TryShowToast(importance, title, message))
                    return NotificationDelivery.InApp;

                // FALLING BACK TO THE BALLOON HERE DOES NOT BREAK THE ROUTER'S RULE. Balloons are
                // kept away from a visible window because one would be a SECOND notification for
                // something already on screen — and there is no first one here, since the toast
                // host is installed by the shell and two of the events wired to this fire while the
                // shell is still loading. App initialization failing is one of them.
                return TryShowBalloon(importance, title, message)
                    ? NotificationDelivery.TrayBalloon
                    : Unannounced(importance, title, "neither the toast host nor the tray balloon would take it");

            case NotificationChannel.TrayBalloon:
                // No fallback the other way: this branch runs only while the window is hidden, so
                // an in-app toast would be shown to a surface nobody is looking at.
                return TryShowBalloon(importance, title, message)
                    ? NotificationDelivery.TrayBalloon
                    : Unannounced(importance, title, "the tray balloon could not be shown");

            default:
                return NotificationDelivery.Suppressed;
        }
    }

    /// <summary>Shows a toast, reporting rather than throwing when the surface cannot take it.</summary>
    /// <remarks>
    /// The sinks are set from outside this class, so "it threw" is as real a failure mode as "it is
    /// not installed" and has to reach the same place. An exception escaping here would surface on
    /// the dispatcher instead, where the one thing it could not do is announce itself.
    /// </remarks>
    private bool TryShowToast(NotificationImportance importance, string title, string message)
    {
        if (InApp is null)
            return false;

        try
        {
            InApp.Show(importance, title, message);
            return true;
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Notification", "In-app toast could not be shown", ex);
            return false;
        }
    }

    /// <inheritdoc cref="TryShowToast" />
    private bool TryShowBalloon(NotificationImportance importance, string title, string message)
    {
        if (TrayBalloon is null)
            return false;

        try
        {
            return TrayBalloon.TryShow(importance, title, message);
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Notification", "Tray balloon could not be shown", ex);
            return false;
        }
    }

    /// <summary>
    /// Whether the main window can be seen right now, treating "cannot tell" as hidden.
    /// </summary>
    /// <remarks>
    /// <b>THE DEFAULT IS DELIBERATE.</b> Close-to-tray is on by default, so an unanswerable probe is
    /// far more likely to be a hidden window than a visible one — and the two mistakes are not
    /// symmetric. Guessing "visible" puts the toast on a surface nobody is looking at and the event
    /// is announced to no one; guessing "hidden" balloons something the user could also have seen in
    /// the window, which is a double notification. One is invisible, the other is merely redundant.
    /// </remarks>
    private bool IsWindowVisible() => WindowVisibleProbe?.Invoke() ?? false;

    private static NotificationDelivery Unannounced(
        NotificationImportance importance, string title, string reason)
    {
        InMemoryLogSink.Append(LogLevel.Warning, "Notification",
            $"A {importance} notification was not shown because {reason}: {title}", null);
        return NotificationDelivery.Failed;
    }
}
