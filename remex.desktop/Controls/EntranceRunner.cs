using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Remex.Desktop.Styles;

namespace Remex.Desktop.Controls;

/// <summary>
/// Plays <see cref="EntranceAnimation"/> on one target, where a replay may start before the previous
/// run has finished (the tray flyout reopened within its own open animation, RemEx-pp4cm.1).
/// </summary>
/// <remarks>
/// <para>
/// OWNERSHIP, NOT JUST CANCELLATION. The old run's <c>finally</c> is an await continuation, and when it
/// runs is not this type's to decide: measured, it runs inline inside <c>Cancel</c> today, but if it
/// were ever posted it would land after the new run had adopted the <c>TranslateTransform</c> the old
/// one installed, and an unconditional clear would pull that transform out from under the reopen,
/// which would fade in with no slide. So only the run that is still current clears the target, and
/// the new run is made current before the old one is cancelled; a superseded run leaves the target
/// to its successor either way. This is the same rule <see cref="ListEntrance"/> applies per row.
/// </para>
/// <para>
/// UI thread only, like everything that touches the target.
/// </para>
/// </remarks>
/// <param name="target">The control to animate.</param>
internal sealed class EntranceRunner(Control target)
{
    private CancellationTokenSource? _current;

    /// <summary>True while a run started by <see cref="Play"/> has not yet finished or been replaced.</summary>
    internal bool IsRunning => _current is not null;

    /// <summary>
    /// Starts the entrance, replacing any run still in progress. A no-op animation under reduced
    /// motion, which still clears anything a superseded run left on the target.
    /// </summary>
    /// <param name="duration">How long the arrival takes.</param>
    /// <param name="delay">How long to hold the target invisible first.</param>
    /// <param name="offset">How far below its resting place the target starts, in DIPs.</param>
    /// <returns>The run, which completes when it finishes or is replaced.</returns>
    internal Task Play(TimeSpan duration, TimeSpan delay, double offset)
    {
        // The new run becomes current BEFORE the old one is cancelled. The old run's cleanup may run
        // inline inside Cancel or on a later dispatcher turn, and either way it must already see that
        // it has been replaced.
        var previous = _current;
        var cts = new CancellationTokenSource();
        _current = cts;
        previous?.Cancel();

        return RunAsync(duration, delay, offset, cts);
    }

    private async Task RunAsync(TimeSpan duration, TimeSpan delay, double offset, CancellationTokenSource cts)
    {
        try
        {
            if (!Motion.IsReducedMotion)
            {
                await EntranceAnimation.Build(duration, delay, offset).RunAsync(target, cts.Token);
            }
        }
        finally
        {
            if (ReferenceEquals(_current, cts))
            {
                _current = null;
                EntranceAnimation.Clear(target);
            }

            cts.Dispose();
        }
    }
}
