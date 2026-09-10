using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Session;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Validation;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that a scroll delta off the wire is bounded before it reaches an input backend (RemEx-hnin).
///
/// THE CONSEQUENCE IS WILDLY OUT OF PROPORTION TO THE INPUT, AND ONLY ON ONE OF THE TWO DISPATCHERS.
/// A client sending <c>deltaY = int.MinValue</c> reached <c>Math.Abs</c> in two of the Linux scroll
/// paths, which has no representable result and throws rather than saturating.
/// <c>RemoteDesktopHandler.DispatchInput</c> catches <c>Win32Exception</c>,
/// <c>InvalidOperationException</c> and <c>ArgumentException</c> — not <c>OverflowException</c> —
/// so it escaped into <c>ProcessInputQueue</c>, whose own catch list is narrower still, ending the
/// <c>GetConsumingEnumerable</c> loop. That thread is started once in the handler's constructor and
/// never restarted, so every later mouse and keyboard event for the session was silently discarded
/// while the video kept streaming: a desktop that looks live and ignores you. The faulted task is
/// then swallowed at teardown by a <c>catch (AggregateException)</c> commented "expected", so nothing
/// named the cause either. <c>PingPongHandler</c> dispatches inline behind a <c>catch (Exception)</c>,
/// so there the same throw cost one event; the containment gap is filed as RemEx-q4wm.
///
/// A magnitude bound rather than an <c>int.MinValue</c> special case, because the overflow was only
/// the loudest symptom: a merely huge delta made the Linux backend router's xdotool fallback spawn
/// one <c>xdotool</c> process per detent, unbounded.
///
/// NOT every scroll path threw. <c>LinuxInputSimulationService</c>'s own xdotool branch negates
/// (<c>-delta</c>) rather than taking a magnitude, and unary minus on <c>int.MinValue</c> is
/// unchecked — it yields <c>int.MinValue</c> again, which the clamp then floors to one click. Wrong,
/// but quietly so. The bead's phrasing said otherwise and is corrected here.
/// </summary>
public sealed class ScrollDeltaClampTests
{
    private sealed class Recorder
    {
        public List<(int DeltaX, int DeltaY)> Scrolls { get; } = [];

        public IInputSimulationService Build()
        {
            var mock = new Mock<IInputSimulationService>();
            mock.Setup(x => x.MouseScroll(It.IsAny<int>(), It.IsAny<int>()))
                .Callback<int, int>((dx, dy) => Scrolls.Add((dx, dy)));
            return mock.Object;
        }
    }

    private static RemoteDesktopHandler NewHandler(IInputSimulationService input) =>
        new(
            NullLogger<RemoteDesktopHandler>.Instance,
            Mock.Of<IScreenCaptureService>(),
            input,
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

    private static InputEvent Scroll(int? deltaX, int? deltaY) =>
        new() { EventType = InputEventTypes.MouseScroll, DeltaX = deltaX, DeltaY = deltaY };

    [Theory]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(int.MaxValue, int.MinValue)]
    [InlineData(-2_000_000_000, 2_000_000_000)]
    public void APathologicalDeltaNeverReachesTheBackend(int deltaX, int deltaY)
    {
        var recorder = new Recorder();
        using var handler = NewHandler(recorder.Build());

        handler.DispatchInput(Scroll(deltaX, deltaY));

        var (sentX, sentY) = Assert.Single(recorder.Scrolls);
        Assert.InRange(sentX, -CoordinateValidation.MaxScrollDelta, CoordinateValidation.MaxScrollDelta);
        Assert.InRange(sentY, -CoordinateValidation.MaxScrollDelta, CoordinateValidation.MaxScrollDelta);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-100, 0)]
    [InlineData(240, -360)]
    public void ARealisticGestureIsPassedThroughUnchanged(int deltaX, int deltaY)
    {
        // The guard must be invisible in normal use, or it is a behaviour change wearing a guard's
        // clothes. These are the magnitudes the Android client actually produces.
        var recorder = new Recorder();
        using var handler = NewHandler(recorder.Build());

        handler.DispatchInput(Scroll(deltaX, deltaY));

        Assert.Equal((deltaX, deltaY), Assert.Single(recorder.Scrolls));
    }

    [Fact]
    public void AScrollWithNoDeltasAtAllStillDispatchesAsANoOp()
    {
        // mouseScroll is the one case in the dispatcher with no `when` guard on its fields, so an
        // event carrying neither delta reaches the backend as (0, 0) rather than being skipped.
        // Recorded because the clamp had to preserve that: null must become 0, not be dropped.
        var recorder = new Recorder();
        using var handler = NewHandler(recorder.Build());

        handler.DispatchInput(Scroll(null, null));

        Assert.Equal((0, 0), Assert.Single(recorder.Scrolls));
    }

    [Fact]
    public void AnUnexpectedBackendFailureNowCostsTheEventRatherThanEscaping()
    {
        // REPLACES a test that asserted the opposite. Until RemEx-q4wm this pinned the negative the
        // severity argument rested on — that OverflowException escaped DispatchInput — with a note
        // saying its author should REPLACE it rather than delete it once containment landed. This is
        // that replacement, kept here so the clamp and the containment stay visibly complementary:
        // the clamp removes the known trigger, this removes the consequence of the next unknown one.
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseScroll(It.IsAny<int>(), It.IsAny<int>()))
            .Throws<OverflowException>();

        using var handler = NewHandler(mock.Object);

        handler.DispatchInput(Scroll(1, 1));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(-2_000_000_000)]
    public void TheYdotoolDetentConversionIsTotalEvenWithoutTheBoundaryClamp(int delta)
    {
        // Defence in depth: IInputSimulationService is a public interface, so a future caller could
        // reach a backend without passing a handler. Math.Abs is taken on a widened long here for
        // that reason, and int.MinValue is the only value that distinguishes the two.
        var detents = LinuxInputSimulationService.WheelDetents(delta);

        Assert.InRange(Math.Abs(detents), 1, 10);
        Assert.Equal(Math.Sign(delta), Math.Sign(detents));
    }
}
