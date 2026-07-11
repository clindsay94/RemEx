using System;

namespace Remex.Agent.Services.RemoteDesktop;

/// <summary>A capture-scale change decided by <see cref="AdaptiveScaleController"/>, with a human-readable reason for logging.</summary>
public sealed record AdaptiveScaleDecision(double Scale, string Reason);

/// <summary>
/// Pure, clock-injected state machine that holds the sharpest H.264 capture scale the host can
/// sustain at the requested frame rate (Phase 5, RemEx-eo0f).
///
/// <para>
/// Without this, a preset's configured scale (e.g. 0.5 for SMOOTH_SHARP) is a fixed ceiling even on
/// hardware that could comfortably encode sharper. The controller starts at that scale and steps up
/// the ladder when the encoder proves it can keep up, stepping back down the moment it can't — so the
/// stream is only as soft as the weakest link (GPU load, thermal throttling, background contention)
/// actually requires, and never softer than that "just in case".
/// </para>
///
/// <para>
/// <see cref="Report"/> is called once per evaluation window (~2s, driven by the caller) with the
/// window's achieved-FPS ratio and whether the encoded-output channel overflowed (Phase 3B). It is
/// pure and clock-injected (<paramref name="nowUtc"/>, mirroring <c>DuplicationReinitThrottle</c>) so
/// the escalation/hysteresis schedule is unit-testable without a real encoder or GPU. Not internally
/// synchronized: the capture loop is the sole caller, exactly like the rest of its per-iteration state.
/// </para>
/// </summary>
public sealed class AdaptiveScaleController
{
    // Deliberately coarse so scale changes are perceptible and worth the encoder-rebuild cost — not
    // so fine-grained that the controller chases noise.
    private static readonly double[] ScaleLadder = { 0.4, 0.5, 0.65, 0.75, 0.85, 1.0 };

    private static readonly TimeSpan MinChangeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedStepUpHold = TimeSpan.FromSeconds(60);

    private const int StepDownAfterConsecutiveLowWindows = 2;
    private const int StepUpAfterConsecutiveStableWindows = 5;
    private const double LowFpsRatio = 0.90;
    private const double StableFpsRatio = 0.98;

    private readonly int _floorIndex;
    private int _rungIndex;
    private int _consecutiveLowWindows;
    private int _consecutiveStableWindows;
    private DateTime _nextChangeAllowedUtc = DateTime.MinValue;
    private DateTime _stepUpHoldUntilUtc = DateTime.MinValue;
    private bool _lastChangeWasStepUp;

    /// <param name="startingScale">The preset's configured scale — the initial rung (nearest ladder value).</param>
    /// <param name="floorScale">Lowest scale the controller may step down to. Clamped into the ladder's range.</param>
    public AdaptiveScaleController(double startingScale, double floorScale = 0.4)
    {
        _floorIndex = NearestRungIndex(Math.Clamp(floorScale, ScaleLadder[0], ScaleLadder[^1]));
        _rungIndex = Math.Max(NearestRungIndex(startingScale), _floorIndex);
    }

    /// <summary>The capture scale the controller currently holds.</summary>
    public double CurrentScale => ScaleLadder[_rungIndex];

    /// <summary>
    /// Reports one evaluation window's results and returns a scale change if one is warranted, or
    /// null if the current scale should be held.
    /// </summary>
    /// <param name="targetFps">The stream's configured target frame rate.</param>
    /// <param name="achievedFps">Frames actually accepted by the encoder during the window (delta of <see cref="IH264Encoder.AcceptedInputFrameCount"/> / window seconds).</param>
    /// <param name="outputOverflowed">True if the encoded-output channel dropped any access units during the window (delta of <see cref="IH264Encoder.DroppedAccessUnitCount"/> &gt; 0).</param>
    /// <param name="nowUtc">Current time, injected for deterministic testing.</param>
    public AdaptiveScaleDecision? Report(int targetFps, double achievedFps, bool outputOverflowed, DateTime nowUtc)
    {
        if (targetFps <= 0) return null;

        double ratio = achievedFps / targetFps;
        bool lowWindow = outputOverflowed || ratio < LowFpsRatio;
        bool stableHighWindow = !outputOverflowed && ratio >= StableFpsRatio;

        _consecutiveLowWindows = lowWindow ? _consecutiveLowWindows + 1 : 0;
        _consecutiveStableWindows = stableHighWindow ? _consecutiveStableWindows + 1 : 0;

        bool wantsStepDown = _rungIndex > _floorIndex &&
            (outputOverflowed || _consecutiveLowWindows >= StepDownAfterConsecutiveLowWindows);
        bool wantsStepUp = _rungIndex < ScaleLadder.Length - 1 &&
            _consecutiveStableWindows >= StepUpAfterConsecutiveStableWindows;

        if (nowUtc < _nextChangeAllowedUtc) return null;

        if (wantsStepDown)
        {
            bool wasFailedStepUp = _lastChangeWasStepUp;
            _rungIndex--;
            _consecutiveLowWindows = 0;
            _consecutiveStableWindows = 0;
            _nextChangeAllowedUtc = nowUtc + MinChangeInterval;
            _lastChangeWasStepUp = false;
            // A step-down that follows closely on the heels of a step-up means that step-up wasn't
            // actually sustainable — hold here longer than the normal cooldown so we don't immediately
            // retry and oscillate between the two rungs.
            if (wasFailedStepUp)
                _stepUpHoldUntilUtc = nowUtc + FailedStepUpHold;
            return new AdaptiveScaleDecision(CurrentScale, outputOverflowed ? "output-overflow" : "low-fps");
        }

        if (wantsStepUp)
        {
            if (nowUtc < _stepUpHoldUntilUtc) return null;

            _rungIndex++;
            _consecutiveLowWindows = 0;
            _consecutiveStableWindows = 0;
            _nextChangeAllowedUtc = nowUtc + MinChangeInterval;
            _lastChangeWasStepUp = true;
            return new AdaptiveScaleDecision(CurrentScale, "fps-stable");
        }

        return null;
    }

    private static int NearestRungIndex(double scale)
    {
        int best = 0;
        double bestDelta = double.MaxValue;
        for (int i = 0; i < ScaleLadder.Length; i++)
        {
            double delta = Math.Abs(ScaleLadder[i] - scale);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = i;
            }
        }
        return best;
    }
}
