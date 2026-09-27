using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.ViewModels;
using Remex.Desktop.Views;

namespace Remex.Desktop.Services.Routines;

/// <summary>
/// The Avalonia implementation of <see cref="IRoutineUi"/>: the countdown window, its tray balloon,
/// the tray menu's "Cancel routine" item, and routed notifications.
/// </summary>
/// <remarks>
/// <para>
/// <b>NEVER THROWS, NEVER NEEDS MainWindow.</b> Every failure becomes "not shown", which the caller
/// records as <c>countdown_unseen</c> and which still lets the 15 s elapse and the phone mirror carry
/// the countdown. The window is created ownerless (see <see cref="RoutineCountdownWindow"/>).
/// </para>
/// <para>
/// Only one countdown runs at a time (the coordinator enforces it), so one window slot is enough.
/// </para>
/// </remarks>
public sealed class AvaloniaRoutineUi : IRoutineUi
{
    private RoutineCountdownWindow? _window;
    private string? _windowRunId;

    /// <inheritdoc />
    public async Task<bool> ShowCountdownAsync(RoutineCountdownPrompt prompt, Action onCancel)
    {
        // The tray item and the balloon do not depend on the window: arm them first, so a window
        // that fails to open still leaves a way to cancel from the tray.
        RoutineCountdownTrayState.Instance.Activate(prompt.RunId, onCancel);

        // THE BALLOON IS NOT ROUTED LIKE AN OUTCOME. It used to be Notify(Outcome), which the router
        // sends to an in-app toast whenever the main window is visible - so with RemEx open no balloon
        // appeared at all (RemEx-pp0rt.16). The spec makes the balloon the guaranteed countdown surface
        // (§8.6; on Wayland the topmost window is only a request), so it goes through the imminent route.
        NotificationService.Instance.NotifyImminent(
            prompt.RoutineName,
            RoutineStrings.Format(
                "Routine_Tray_CountdownMessage", RoutineStrings.ActionLabel(prompt.Verb), prompt.Seconds));

        if (Application.Current is null)
        {
            return false;
        }

        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CloseWindowOnUiThread();
                var viewModel = new RoutineCountdownViewModel(prompt, onCancel);
                var window = new RoutineCountdownWindow(viewModel);
                window.Closed += (_, _) =>
                {
                    // Closed by the person (a Cancel): drop the slot so the coordinator's later close
                    // does not reach for a dead window.
                    if (ReferenceEquals(_window, window))
                    {
                        _window = null;
                        _windowRunId = null;
                    }
                };
                window.ShowCentred();
                _window = window;
                _windowRunId = prompt.RunId;
                return window.IsVisible;
            });
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Routines", "The routine countdown window could not be shown", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void CloseCountdown(string runId)
    {
        RoutineCountdownTrayState.Instance.Deactivate(runId);

        if (Application.Current is null)
        {
            return;
        }

        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(_windowRunId, runId, StringComparison.Ordinal))
                {
                    CloseWindowOnUiThread();
                }
            });
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Routines", "The routine countdown window could not be closed", ex);
        }
    }

    /// <inheritdoc />
    public void Notify(NotificationImportance importance, string title, string message)
    {
        try
        {
            NotificationService.Instance.Notify(importance, title, message);
        }
        catch (Exception ex)
        {
            InMemoryLogSink.Append(LogLevel.Warning, "Routines", "A routine notification could not be shown", ex);
        }
    }

    /// <summary>The countdown window currently shown, for tests. UI thread only.</summary>
    internal RoutineCountdownWindow? CurrentWindow => _window;

    // Only the coordinator reaches this (CloseCountdown), or a new countdown replacing a finished one;
    // either way the countdown is over, so this close must not be a Cancel. Every other close is.
    private void CloseWindowOnUiThread()
    {
        var window = _window;
        _window = null;
        _windowRunId = null;
        window?.CloseAfterCountdownEnded();
    }
}
