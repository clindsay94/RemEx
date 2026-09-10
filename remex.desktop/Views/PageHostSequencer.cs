namespace Remex.Desktop.Views;

/// <summary>
/// Decides when the shell's content host may be handed the next page, so that a navigation can never
/// interrupt the transition started by the one before it.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia 12.1.1's <c>TransitioningContentControl</c> cannot be interrupted safely, and repairing
/// the damage afterwards does not work because the repair arrives too late. When
/// <c>ArrangeOverride</c> cancels the transition in flight it does not wait for it: the cancelled
/// transition's continuation is queued on the dispatcher, while <c>ArrangeOverride</c> carries
/// straight on to construct and <c>Start</c> the successor in the same call. The old transition's
/// cleanup then runs on top of a transition that is already animating (RemEx-lma2o).
/// </para>
/// <para>
/// So interruption is removed rather than survived. While a transition is running the newest
/// requested view is held back; when the host reports the transition finished, the held view is
/// applied. Only the newest is kept — a burst of clicks should land on the page the user stopped on,
/// not replay every page they passed through — so a burst costs one extra transition, never a queue
/// of them.
/// </para>
/// <para>
/// This type is deliberately free of Avalonia types: the caller owns the host and the timer, and this
/// owns only the decision, which is the part worth testing.
/// </para>
/// </remarks>
internal sealed class PageHostSequencer
{
    private object? _pendingView;

    /// <summary>True while a transition this sequencer started is believed to still be running.</summary>
    internal bool IsBusy { get; private set; }

    /// <summary>True while a navigation is being held back until the current transition finishes.</summary>
    internal bool HasPendingView { get; private set; }

    /// <summary>Records a navigation request.</summary>
    /// <param name="view">The page the shell wants to show.</param>
    /// <returns><c>true</c> when the caller should hand <paramref name="view"/> to the host now.</returns>
    internal bool RequestShow(object? view)
    {
        if (IsBusy)
        {
            _pendingView = view;
            HasPendingView = true;
            return false;
        }

        _pendingView = null;
        HasPendingView = false;
        IsBusy = true;
        return true;
    }

    /// <summary>
    /// Records that the host has finished, abandoned, or failed to report a transition.
    /// </summary>
    /// <param name="view">The held-back page, when there is one.</param>
    /// <returns><c>true</c> when the caller should hand <paramref name="view"/> to the host now.</returns>
    internal bool RequestFlush(out object? view)
    {
        IsBusy = false;
        view = null;

        if (!HasPendingView)
        {
            return false;
        }

        view = _pendingView;
        _pendingView = null;
        HasPendingView = false;
        IsBusy = true;
        return true;
    }
}
