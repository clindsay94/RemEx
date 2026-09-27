using System;
using System.Threading;

namespace Remex.Desktop.Controls.Splash;

/// <summary>
/// The splash's own clock (RemEx-8g6n0.2 fix round). Two modes:
/// <list type="bullet">
/// <item>FIXED FILMS (Cosmic Zoom, Pong, RemEx Command): exactly the old behaviour — the clock runs
/// from attach and every tick adds the real elapsed wall time, unclamped.</item>
/// <item>LIVE (Live Handshake): the clock stays at 0 until the first frame has actually been rendered,
/// and each step is clamped to <see cref="LiveMaxStep"/>. Measured on a Debug build: the window became
/// visible ~3.6 s after launch while the clock had been running since attach, so the first presented
/// frame was already at the hand-off and the splash showed for ~0.3 s; and a stall (first shader
/// compile, first GPU frame, a GC) jumped the timeline. Events observed before the first frame are
/// stamped at t = 0 because the clock reads 0 then, and the director's CAP runs from the first frame.</item>
/// </list>
/// <see cref="MarkFirstFrame"/> is called from the render thread; everything else from the UI thread.
/// </summary>
public sealed class SplashClock
{
    /// <summary>Largest step a live splash takes per tick: a stall slows it down, never skips it ahead.</summary>
    public const double LiveMaxStep = 1.0 / 30.0;

    /// <summary>
    /// Backstop for a live splash whose first frame never renders (a window that stays hidden, e.g.
    /// started to the tray): after this much time waiting, the clock starts anyway so the splash still
    /// completes on its own.
    /// </summary>
    public const double FirstFrameTimeout = 5.0;

    private int _firstFrameRendered;
    private bool _live;
    private double _waited;

    /// <summary>Seconds of splash time.</summary>
    public double Elapsed { get; private set; }

    /// <summary>The step applied by the last <see cref="Advance"/>.</summary>
    public double LastDt { get; private set; }

    /// <summary>True once the clock is moving (always true for a fixed film).</summary>
    public bool Running => !_live || Volatile.Read(ref _firstFrameRendered) != 0;

    /// <summary>
    /// True once a frame has been rendered this sequence (either mode), or once a live clock gave up
    /// waiting for one after <see cref="FirstFrameTimeout"/>.
    /// </summary>
    public bool HasRendered => Volatile.Read(ref _firstFrameRendered) != 0;

    /// <summary>Back to 0. <paramref name="live"/> selects the live rules described on the class.</summary>
    public void Reset(bool live)
    {
        _live = live;
        _waited = 0;
        Elapsed = 0;
        LastDt = 0;
        Volatile.Write(ref _firstFrameRendered, 0);
    }

    /// <summary>A frame has been rendered (render thread). Idempotent.</summary>
    public void MarkFirstFrame() => Volatile.Write(ref _firstFrameRendered, 1);

    /// <summary>
    /// Advances by <paramref name="wallDt"/> seconds of real time, under the mode's rules, and returns
    /// the step actually applied to <see cref="Elapsed"/>.
    /// </summary>
    public double Advance(double wallDt)
    {
        if (double.IsNaN(wallDt) || wallDt < 0) wallDt = 0;
        if (!_live)
        {
            LastDt = wallDt;
            Elapsed += wallDt;
            return wallDt;
        }

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
