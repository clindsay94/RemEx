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
        NotificationService.Instance.Notify(
            NotificationImportance.Outcome,
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
                window.Show();
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

    private void CloseWindowOnUiThread()
    {
        var window = _window;
        _window = null;
        _windowRunId = null;
        window?.Close();
    }
}
