namespace Remex.Desktop.Services.Routines;

/// <summary>
/// Whether a routine countdown is running, for the tray menu's "Cancel routine" item (routines spec
/// §8.6, "Cancel paths").
/// </summary>
/// <remarks>
/// <para>
/// A process-wide singleton, like <see cref="NotificationService"/>, because the tray menu is built by
/// <c>App</c> and the countdown is started by the agent, and neither should hold the other. The tray
/// is the one cancel surface that exists whether or not the main window was ever constructed, which
/// is the state a <c>--minimized</c> logon start leaves the app in.
/// </para>
/// <para>
/// Thread-safe: set from the agent's side, read and invoked from the UI thread. <see cref="Changed"/>
/// is raised on whichever thread made the change; subscribers marshal to the UI thread themselves.
/// </para>
/// </remarks>
public sealed class RoutineCountdownTrayState
{
    private static readonly Lazy<RoutineCountdownTrayState> _instance = new(() => new RoutineCountdownTrayState());

    private readonly object _gate = new();
    private string? _runId;
    private Action? _cancel;

    /// <summary>The process-wide instance.</summary>
    public static RoutineCountdownTrayState Instance => _instance.Value;

    /// <summary>Raised whenever a countdown starts or ends.</summary>
    public event Action? Changed;

    /// <summary>True while a countdown can be cancelled from the tray.</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _runId is not null;
            }
        }
    }

    /// <summary>Arms the tray item for <paramref name="runId"/>.</summary>
    public void Activate(string runId, Action cancel)
    {
        lock (_gate)
        {
            _runId = runId;
            _cancel = cancel;
        }

        Changed?.Invoke();
    }

    /// <summary>Disarms the tray item, but only if it still belongs to <paramref name="runId"/>.</summary>
    public void Deactivate(string runId)
    {
        lock (_gate)
        {
            if (!string.Equals(_runId, runId, StringComparison.Ordinal))
            {
                return;
            }

            _runId = null;
            _cancel = null;
        }

        Changed?.Invoke();
    }

    /// <summary>The tray menu's click: cancels the active countdown, if any. Returns whether one was.</summary>
    public bool CancelActive()
    {
        Action? cancel;
        lock (_gate)
        {
            cancel = _cancel;
        }

        if (cancel is null)
        {
            return false;
        }

        cancel();
        return true;
    }
}
