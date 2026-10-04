using System;
using System.Threading;

namespace Remex.Desktop.Controls.Splash;

/// <summary>
/// The splash's own clock (RemEx-8g6n0.2 fix round; every style since RemEx-pp4cm.11). It stays at 0
/// until the first frame has actually been rendered, and each step is clamped to
/// <see cref="LiveMaxStep"/>. Measured on a Debug build: the window became visible ~3.6 s after launch
/// while a clock started at attach had been running all along, so the first presented frame was
/// already at the hand-off. Live Handshake got this rule first; the fixed films (Cosmic Zoom, Pong,
/// RemEx Command, each ~3 s) kept running from attach and so played mostly unseen on a cold start,
/// which is why they now share it. A stall (first shader compile, first GPU frame, a GC) slows the
/// timeline rather than jumping it. Events observed before the first frame are stamped at t = 0
/// because the clock reads 0 then, and the director's CAP runs from the first frame.
/// <see cref="MarkFirstFrame"/> is called from the render thread; everything else from the UI thread.
/// </summary>
public sealed class SplashClock
{
    /// <summary>Largest step the clock takes per tick: a stall slows it down, never skips it ahead.</summary>
    public const double LiveMaxStep = 1.0 / 30.0;

    /// <summary>
    /// Backstop for a splash whose first frame never renders (a window that stays hidden, e.g. started
    /// to the tray): after this much time waiting, the clock starts anyway so the splash still
    /// completes on its own.
    /// </summary>
    public const double FirstFrameTimeout = 5.0;

    private int _firstFrameRendered;
    private double _waited;

    /// <summary>Seconds of splash time.</summary>
    public double Elapsed { get; private set; }

    /// <summary>The step applied by the last <see cref="Advance"/>.</summary>
    public double LastDt { get; private set; }

    /// <summary>True once the clock is moving: a frame has rendered, or the wait timed out.</summary>
    public bool Running => Volatile.Read(ref _firstFrameRendered) != 0;

    /// <summary>
    /// True once a frame has been rendered this sequence, or once the clock gave up waiting for one
    /// after <see cref="FirstFrameTimeout"/>.
    /// </summary>
    public bool HasRendered => Volatile.Read(ref _firstFrameRendered) != 0;

    /// <summary>Back to 0, waiting for a fresh first frame (a new style, or the Preview replay).</summary>
    public void Reset()
    {
        _waited = 0;
        Elapsed = 0;
        LastDt = 0;
        Volatile.Write(ref _firstFrameRendered, 0);
    }

    /// <summary>A frame has been rendered (render thread). Idempotent.</summary>
    public void MarkFirstFrame() => Volatile.Write(ref _firstFrameRendered, 1);

    /// <summary>
    /// Advances by <paramref name="wallDt"/> seconds of real time under the rules on the class, and
    /// returns the step actually applied to <see cref="Elapsed"/>.
    /// </summary>
    public double Advance(double wallDt)
    {
        if (double.IsNaN(wallDt) || wallDt < 0) wallDt = 0;

        if (!Running)
        {
            _waited += wallDt;
            if (_waited < FirstFrameTimeout)
            {
                LastDt = 0;
                return 0;
            }
            MarkFirstFrame();
            // The step that crosses the timeout is the one that waited; time starts on the next.
            LastDt = 0;
            return 0;
        }

        double step = Math.Min(wallDt, LiveMaxStep);
        LastDt = step;
        Elapsed += step;
        return step;
    }
}
