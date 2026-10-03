using System;
using System.Threading;
using Avalonia.Animation;
using Avalonia.Animation.Easings;

namespace Remex.Desktop.Styles;

/// <summary>
/// The PC's motion tokens: every easing curve and duration a transition or animation in
/// remex.desktop uses, in one place (RemEx-pp4cm.1). XAML reaches them with
/// <c>{x:Static motion:Motion.StateChange}</c> / <c>{x:Static motion:Motion.Standard}</c>, code
/// reads the fields directly.
/// </summary>
/// <remarks>
/// <para>
/// The curves and durations are Material 3's, so the PC moves the way the phone's M3 Expressive pass
/// does (<c>remex.android/.../ui/theme/Motion.kt</c>): emphasized-decelerate for things arriving,
/// emphasized-accelerate for things leaving, standard for things changing in place. There is no
/// spring and no overshoot on the PC; desktop motion here is quick and purposeful.
/// </para>
/// <para>
/// REDUCED MOTION IS IN THE EASING, NOT THE DURATION. Every easing below is a
/// <see cref="MotionEasing"/>, which reports full progress (1.0) from the first frame while
/// <see cref="IsReducedMotion"/> is set, so a transition that uses it lands on its end value
/// immediately: instant, not merely faster. Because the easing reads the flag on every frame, the
/// Personalize switch takes effect on the next property change without a restart, for transitions
/// declared as local values and in any window, which a class-gated style could not reach (a view's
/// own <c>UserControl.Styles</c> are applied after Application.Styles and win any tie). Code-driven
/// animations (page transitions, list entrances) check <see cref="IsReducedMotion"/> or take a
/// <c>reducedMotion</c> argument and skip the animation entirely.
/// </para>
/// </remarks>
public static class Motion
{
    // ═══ Material 3 duration scale ═══

    /// <summary>M3 short1, 50 ms.</summary>
    public static readonly TimeSpan Short1 = TimeSpan.FromMilliseconds(50);

    /// <summary>M3 short2, 100 ms.</summary>
    public static readonly TimeSpan Short2 = TimeSpan.FromMilliseconds(100);

    /// <summary>M3 short3, 150 ms.</summary>
    public static readonly TimeSpan Short3 = TimeSpan.FromMilliseconds(150);

    /// <summary>M3 short4, 200 ms.</summary>
    public static readonly TimeSpan Short4 = TimeSpan.FromMilliseconds(200);

    /// <summary>M3 medium1, 250 ms.</summary>
    public static readonly TimeSpan Medium1 = TimeSpan.FromMilliseconds(250);

    /// <summary>M3 medium2, 300 ms.</summary>
    public static readonly TimeSpan Medium2 = TimeSpan.FromMilliseconds(300);

    /// <summary>M3 medium3, 350 ms.</summary>
    public static readonly TimeSpan Medium3 = TimeSpan.FromMilliseconds(350);

    /// <summary>M3 medium4, 400 ms.</summary>
    public static readonly TimeSpan Medium4 = TimeSpan.FromMilliseconds(400);

    /// <summary>M3 long1, 450 ms.</summary>
    public static readonly TimeSpan Long1 = TimeSpan.FromMilliseconds(450);

    /// <summary>M3 long2, 500 ms.</summary>
    public static readonly TimeSpan Long2 = TimeSpan.FromMilliseconds(500);

    /// <summary>M3 extra-long4, 1000 ms. Ambient backdrop fades only, never a control.</summary>
    public static readonly TimeSpan ExtraLong4 = TimeSpan.FromMilliseconds(1000);

    // ═══ What each duration is for ═══

    /// <summary>Hover, press and focus state layers: colour, border and press-scale changes.</summary>
    public static readonly TimeSpan StateChange = Short3;

    /// <summary>A selection indicator or toggle moving to its new state.</summary>
    public static readonly TimeSpan Selection = Short4;

    /// <summary>A card rising a level (translate, scale and shadow) under the pointer.</summary>
    public static readonly TimeSpan Lift = Medium1;

    /// <summary>A larger in-place change: an expander opening, a palette cross-fade.</summary>
    public static readonly TimeSpan InPlace = Medium2;

    /// <summary>A page, dialog or surface arriving (emphasized-decelerate).</summary>
    public static readonly TimeSpan Enter = Medium4;

    /// <summary>A page, dialog or surface leaving (emphasized-accelerate).</summary>
    public static readonly TimeSpan Exit = Short4;

    /// <summary>One list row fading and sliding in as it appears.</summary>
    public static readonly TimeSpan ListItemEnter = Medium2;

    /// <summary>The gap between consecutive rows of one list entrance.</summary>
    public static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// Rows past this index share the last row's delay, so a list entrance never waits more than
    /// <c>(MaxStaggerSteps) × StaggerStep</c> before its last row starts moving.
    /// </summary>
    public const int MaxStaggerSteps = 5;

    /// <summary>
    /// The most rows one burst of additions may hold and still animate. A bigger burst is a reload,
    /// and a reload of many rows appears at once rather than as a slow cascade.
    /// </summary>
    public const int MaxAnimatedBatch = 12;

    /// <summary>How far a list row travels as it enters, in device-independent pixels.</summary>
    public const double ListItemOffset = 8d;

    // ═══ Material 3 easing curves (cubic-bezier control points) ═══

    /// <summary>M3 standard, cubic-bezier(0.2, 0, 0, 1): in-place changes.</summary>
    public static readonly Easing Standard = new MotionEasing(0.2, 0, 0, 1);

    /// <summary>M3 standard-decelerate, cubic-bezier(0, 0, 0, 1).</summary>
    public static readonly Easing StandardDecelerate = new MotionEasing(0, 0, 0, 1);

    /// <summary>M3 standard-accelerate, cubic-bezier(0.3, 0, 1, 1).</summary>
    public static readonly Easing StandardAccelerate = new MotionEasing(0.3, 0, 1, 1);

    /// <summary>M3 emphasized-decelerate, cubic-bezier(0.05, 0.7, 0.1, 1): things arriving.</summary>
    public static readonly Easing EmphasizedDecelerate = new MotionEasing(0.05, 0.7, 0.1, 1);

    /// <summary>M3 emphasized-accelerate, cubic-bezier(0.3, 0, 0.8, 0.15): things leaving.</summary>
    public static readonly Easing EmphasizedAccelerate = new MotionEasing(0.3, 0, 0.8, 0.15);

    /// <summary>The standard curve as a key-frame spline (a new instance; key splines are mutable).</summary>
    public static KeySpline StandardSpline() => new(0.2, 0, 0, 1);

    /// <summary>The emphasized-decelerate curve as a key-frame spline.</summary>
    public static KeySpline EmphasizedDecelerateSpline() => new(0.05, 0.7, 0.1, 1);

    /// <summary>The emphasized-accelerate curve as a key-frame spline.</summary>
    public static KeySpline EmphasizedAccelerateSpline() => new(0.3, 0, 0.8, 0.15);

    // ═══ Reduced motion ═══

    private static int _isReducedMotion;

    /// <summary>
    /// The user's Reduced motion preference, mirrored here by <c>ShellViewModel</c> so that easings,
    /// which have no view model to bind to, can read it. Process-wide; UI-thread written.
    /// </summary>
    public static bool IsReducedMotion => Volatile.Read(ref _isReducedMotion) != 0;

    /// <summary>Records the user's Reduced motion preference. Takes effect on the next frame.</summary>
    public static void SetReducedMotion(bool reduced) =>
        Volatile.Write(ref _isReducedMotion, reduced ? 1 : 0);

    /// <summary>
    /// <paramref name="duration"/>, or <see cref="TimeSpan.Zero"/> under reduced motion. For code
    /// that builds its own animation: reduced motion means none at all, not a shorter one.
    /// </summary>
    public static TimeSpan Resolve(TimeSpan duration, bool reducedMotion) =>
        reducedMotion ? TimeSpan.Zero : duration;

    /// <summary>
    /// The start delay for the row at <paramref name="index"/> of one list entrance:
    /// <see cref="StaggerStep"/> per row, capped at <see cref="MaxStaggerSteps"/> steps, and zero
    /// under reduced motion.
    /// </summary>
    public static TimeSpan StaggerDelay(int index, bool reducedMotion)
    {
        if (reducedMotion || index <= 0)
        {
            return TimeSpan.Zero;
        }

        return StaggerStep * Math.Min(index, MaxStaggerSteps);
    }

    /// <summary>
    /// Whether a burst of <paramref name="batchSize"/> newly added rows should animate in: only
    /// when motion is not reduced, the window is on screen, and the burst is small enough to read as
    /// rows appearing rather than a list reloading.
    /// </summary>
    public static bool ShouldAnimateBatch(int batchSize, bool reducedMotion, bool windowVisible) =>
        !reducedMotion && windowVisible && batchSize > 0 && batchSize <= MaxAnimatedBatch;
}

/// <summary>
/// A cubic-bezier easing that collapses to "already finished" under reduced motion.
/// </summary>
/// <remarks>
/// <see cref="Ease"/> returns 1.0 for every progress value while the reduced-motion source reports
/// true, so the property a transition drives is at its end value from the first frame. The source is
/// read on every call, which is what makes the preference live.
/// </remarks>
public sealed class MotionEasing : Easing
{
    private readonly SplineEasing _curve;
    private readonly Func<bool> _isReducedMotion;

    /// <summary>A curve that follows <see cref="Motion.IsReducedMotion"/>.</summary>
    public MotionEasing(double x1, double y1, double x2, double y2)
        : this(x1, y1, x2, y2, static () => Motion.IsReducedMotion)
    {
    }

    /// <summary>A curve that follows the given reduced-motion source. Tests use this.</summary>
    internal MotionEasing(double x1, double y1, double x2, double y2, Func<bool> isReducedMotion)
    {
        _curve = new SplineEasing(x1, y1, x2, y2);
        _isReducedMotion = isReducedMotion;
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
    }

    /// <summary>The first control point's x.</summary>
    public double X1 { get; }

    /// <summary>The first control point's y.</summary>
    public double Y1 { get; }

    /// <summary>The second control point's x.</summary>
    public double X2 { get; }

    /// <summary>The second control point's y.</summary>
    public double Y2 { get; }

    /// <inheritdoc />
    public override double Ease(double progress) =>
        _isReducedMotion() ? 1d : _curve.Ease(progress);
}
