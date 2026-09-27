namespace Remex.Desktop.Services.Routines;

/// <summary>What the PC countdown surfaces show for one destructive routine step (routines spec §8.6).</summary>
/// <param name="RunId">The run the countdown belongs to.</param>
/// <param name="RoutineName">The routine's name as the phone sent it (≤ 40, already sanitized).</param>
/// <param name="Verb">The D1 power verb that will be issued, e.g. <c>SHUTDOWN</c>.</param>
/// <param name="PhoneName">The owner phone's display name, or null when the PC has none on file.</param>
/// <param name="Source">The run source (§8.8), e.g. <c>nfc.tap</c>; display only.</param>
/// <param name="Seconds">Countdown length in whole seconds.</param>
/// <param name="TestRun">A phone Test: the verb will not be issued when the countdown ends.</param>
/// <param name="DryRun">The PC is in <c>--routines-dry-run</c>: the verb will only be logged.</param>
public sealed record RoutineCountdownPrompt(
    string RunId,
    string RoutineName,
    string Verb,
    string? PhoneName,
    string? Source,
    int Seconds,
    bool TestRun,
    bool DryRun);

/// <summary>
/// The PC-side surfaces a routine step touches: the countdown window, its tray balloon and tray menu
/// item, and plain notifications (<c>notify(pc)</c>, failures). The step executor in the agent talks
/// to this; the Avalonia implementation is <see cref="AvaloniaRoutineUi"/>.
/// </summary>
/// <remarks>
/// An interface so the executor and the countdown coordinator are testable without a dispatcher
/// (routines spec §13.2, "IRoutineUi"). Every member must be safe to call from any thread and must
/// never throw: a UI failure is a countdown nobody saw (<c>countdown_unseen</c>), never a lost step.
/// </remarks>
public interface IRoutineUi
{
    /// <summary>
    /// Shows the countdown window, the tray balloon and the tray menu's "Cancel routine" item.
    /// </summary>
    /// <param name="prompt">What to show.</param>
    /// <param name="onCancel">
    /// Invoked (on the UI thread) when the person at the PC cancels, from the window or the tray menu.
    /// </param>
    /// <returns>True when the countdown window was actually displayed.</returns>
    Task<bool> ShowCountdownAsync(RoutineCountdownPrompt prompt, Action onCancel);

    /// <summary>Closes the countdown window and removes the tray item for <paramref name="runId"/>.</summary>
    void CloseCountdown(string runId);

    /// <summary>Announces an event through <see cref="NotificationService"/> (router-chosen surface).</summary>
    void Notify(NotificationImportance importance, string title, string message);
}
