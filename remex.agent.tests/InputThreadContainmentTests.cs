using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Session;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that one bad input event costs that event and not the whole session (RemEx-q4wm).
///
/// WHAT THIS PROTECTS IS A THREAD, NOT A LINE OF CODE. <c>ProcessInputQueue</c> is the only consumer
/// of a remote-desktop session's input queue. It is created once, in the constructor, and nothing
/// anywhere restarts it. So an exception escaping <c>DispatchInput</c> did not drop an event — it
/// ended the <c>GetConsumingEnumerable</c> loop, after which every mouse and keyboard event the
/// client sent was queued and never drained, for the life of the connection, WHILE THE VIDEO KEPT
/// STREAMING. The remote user sees a live desktop that ignores them.
///
/// It was invisible from every angle. The dispatcher's catch list named three exception types and
/// could never have been complete: behind that switch are three platform backends that shell out,
/// P/Invoke and talk to a portal over D-Bus. The queue's own catch list was narrower still. And the
/// faulted task was observed only at teardown, by a <c>catch (AggregateException)</c> whose entire
/// body was the comment "Expected if the task faulted or was already completed", after which the
/// code logged that the queue had not drained "within 2s" — blaming a slow dispatch for a thread
/// that had been dead for minutes.
///
/// RemEx-hnin removed the one known trigger (an unclamped scroll delta reaching
/// <c>Math.Abs(int.MinValue)</c>). This is the other half: the next unknown trigger costs an event.
/// </summary>
public sealed class InputThreadContainmentTests
{
    /// <summary>
    /// How long a test waits for the input thread to do something before calling it dead.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. Every wait here asserts LIVENESS — that the consuming thread is still
    /// there at all — and none of them asserts latency, so there is nothing to be gained by a tight
    /// bound and a flake to be lost by one. These run against a real background thread alongside two
    /// other test assemblies, and a solution-wide run under CPU contention produced exactly one
    /// spurious failure at five seconds. A dead thread still fails deterministically; it just takes
    /// longer to say so, and only on the failing path.
    /// </remarks>
    private static readonly TimeSpan LivenessBudget = TimeSpan.FromSeconds(30);

    private static RemoteDesktopHandler NewHandler(IInputSimulationService input) =>
        new(
            NullLogger<RemoteDesktopHandler>.Instance,
            Mock.Of<IScreenCaptureService>(),
            input,
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

    private static DesktopPointerSample Sample(PointerPhase phase) => new() { Phase = phase };

    [Fact]
    public void AThrowingBackendDoesNotStopTheQueueFromDrainingLaterEvents()
    {
        // THE TEST THIS BEAD EXISTS FOR. Everything else here is a detail; this is the claim.
        //
        // It deliberately goes through the REAL queue and the REAL consuming thread rather than
        // calling DispatchInput directly, because "the exception is caught" and "the thread survives"
        // are two different propositions and only the second one was ever in doubt. Reaching the
        // queue is what EnqueuePointerSampleAsInputEvent's internal visibility is for.
        var released = new ManualResetEventSlim(false);
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseDown(It.IsAny<int>())).Throws<OverflowException>();
        mock.Setup(x => x.MouseUp(It.IsAny<int>())).Callback(() => released.Set());

        using var handler = NewHandler(mock.Object);

        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactStart)); // throws
        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactEnd));   // must still run

        Assert.True(
            released.Wait(LivenessBudget),
            "the input thread died on the first event, so the second was never dispatched — which is " +
            "the whole failure this guards: a session that keeps streaming video and ignores input");
    }

    [Fact]
    public void TheQueueKeepsDrainingAcrossManyConsecutiveFailures()
    {
        // A single recovery could be luck — a thread that faults on the *second* throw would still
        // pass the test above. Ten in a row is the loop genuinely continuing rather than surviving
        // once.
        const int failures = 10;
        var released = new ManualResetEventSlim(false);
        var attempts = 0;
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseDown(It.IsAny<int>()))
            .Callback(() => Interlocked.Increment(ref attempts))
            .Throws<InvalidProgramException>();
        mock.Setup(x => x.MouseUp(It.IsAny<int>())).Callback(() => released.Set());

        using var handler = NewHandler(mock.Object);

        for (var i = 0; i < failures; i++)
        {
            handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactStart));
        }

        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactEnd));

        Assert.True(released.Wait(LivenessBudget), "the queue stopped draining partway");
        Assert.Equal(failures, Volatile.Read(ref attempts));
    }

    [Fact]
    public void ACancellationFromABackendIsContainedLikeAnyOtherFailure()
    {
        // THIS TEST ASSERTED THE OPPOSITE ONE REVISION AGO, and the reason is worth keeping. The
        // first draft excluded OperationCanceledException from the catch-all "so shutdown still
        // unwinds", pointing at ProcessInputQueue's arm that logs cancellation as a graceful stop.
        // That arm is unreachable from the queue: it is constructed and enumerated with NO
        // cancellation token, and shutdown is Dispose calling CompleteAdding, which ends the foreach
        // normally. So the exclusion preserved nothing and instead built a fresh silent path — a
        // backend OCE would end the session's input while the log said it stopped gracefully, which
        // is the exact defect this bead exists to remove, reintroduced by its own fix.
        //
        // NO SINGLE MUTATION FAILS THIS TEST, and that is a property of the design rather than a gap:
        // reinstating the carve-out alone leaves the call-site guard to catch the cancellation, and
        // removing the call-site guard alone leaves the dispatcher's unfiltered catch-all to do it.
        // Both have to go before a backend cancellation can reach the "cancelled gracefully" arm and
        // end the loop, which is the whole reason there are two guards. What is pinned here is the
        // composite guarantee, including the half that made the original mistake attractive: the
        // cancellation must NOT be reported as an orderly shutdown.
        var logger = new FailsOnceLogger();
        var released = new ManualResetEventSlim(false);
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseDown(It.IsAny<int>())).Throws<OperationCanceledException>();
        mock.Setup(x => x.MouseUp(It.IsAny<int>())).Callback(() => released.Set());

        using var handler = new RemoteDesktopHandler(
            logger,
            Mock.Of<IScreenCaptureService>(),
            mock.Object,
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactStart));
        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactEnd));

        Assert.True(
            released.Wait(LivenessBudget),
            "a cancellation from a backend still killed the input thread");

        var written = logger.Snapshot();
        Assert.Contains(written, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(written, e => e.Message.Contains("cancelled gracefully"));
    }

    [Fact]
    public void AnEventTheBackendRejectsNormallyIsStillHandledByItsOwnArmAtWarning()
    {
        // Non-regression for the three specific arms that were already there, and it has to assert
        // the LEVEL to mean anything: absence of a throw alone would stay green if all three arms
        // were deleted and the catch-all swallowed everything. They log at Warning; the catch-all
        // logs at Error precisely because reaching it means nobody predicted the failure. Losing that
        // distinction would turn every routine Win32 input rejection into an Error.
        var logger = new Mock<ILogger<RemoteDesktopHandler>>();
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseUp(It.IsAny<int>())).Throws<ArgumentOutOfRangeException>();

        using var handler = new RemoteDesktopHandler(
            logger.Object,
            Mock.Of<IScreenCaptureService>(),
            mock.Object,
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseUp, Button = 0 });

        Verify(logger, LogLevel.Warning, Times.Once());
        Verify(logger, LogLevel.Error, Times.Never());
    }

    [Fact]
    public void AnUnpredictedFailureIsLoggedAtErrorSoItIsDistinguishableFromTheRoutineOnes()
    {
        // The other half of the pair above. An exception outside the three known arms is exactly the
        // case with no prior record anywhere, so the log line is the only evidence the failure class
        // exists — Warning would bury it among ordinary rejections.
        var logger = new Mock<ILogger<RemoteDesktopHandler>>();
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseUp(It.IsAny<int>())).Throws<InvalidProgramException>();

        using var handler = new RemoteDesktopHandler(
            logger.Object,
            Mock.Of<IScreenCaptureService>(),
            mock.Object,
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseUp, Button = 0 });

        Verify(logger, LogLevel.Error, Times.Once());
        Verify(logger, LogLevel.Warning, Times.Never());
    }

    /// <summary>
    /// A logger that throws the first time it is asked to write at each nominated level, then behaves.
    /// </summary>
    /// <remarks>
    /// The point is to reach the two arms that are otherwise unreachable by construction. A throw from
    /// INSIDE one of <c>DispatchInput</c>'s own catch arms leaves that method entirely — sibling catch
    /// clauses do not catch each other — and the only such code is the logging call and the Windows
    /// hint it builds. A fake that fails once rather than always is what keeps the rest of the run
    /// observable: a permanently throwing logger would take out the very Critical and teardown lines
    /// being asserted on.
    /// </remarks>
    private sealed class FailsOnceLogger(params LogLevel[] failAt) : ILogger<RemoteDesktopHandler>
    {
        private readonly HashSet<LogLevel> _pending = [.. failAt];

        public List<(LogLevel Level, string Message)> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_pending)
            {
                if (_pending.Remove(logLevel))
                {
                    throw new InvalidTimeZoneException($"logging provider failed at {logLevel}");
                }
            }

            lock (Written)
            {
                Written.Add((logLevel, formatter(state, exception)));
            }
        }

        public List<(LogLevel Level, string Message)> Snapshot()
        {
            lock (Written)
            {
                return [.. Written];
            }
        }
    }

    [Fact]
    public void AThrowFromInsideTheDispatchersOwnHandlerStillDoesNotEndTheLoop()
    {
        // The case the catch-all in DispatchInput CANNOT cover, which is why there is a second guard
        // around the call site. Here the backend raises an ArgumentException, that arm runs, and its
        // LogWarning throws — so the exception leaves DispatchInput past all of its handlers. Before
        // the call-site guard this ended the consuming loop exactly like the original bug.
        var logger = new FailsOnceLogger(LogLevel.Warning);
        var released = new ManualResetEventSlim(false);
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseDown(It.IsAny<int>())).Throws<ArgumentOutOfRangeException>();
        mock.Setup(x => x.MouseUp(It.IsAny<int>())).Callback(() => released.Set());

        using var handler = new RemoteDesktopHandler(
            logger,
            Mock.Of<IScreenCaptureService>(),
            mock.Object,
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactStart));
        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactEnd));

        Assert.True(
            released.Wait(LivenessBudget),
            "a throw from inside the dispatcher's own catch arm ended the input thread");
        Assert.Contains(logger.Snapshot(), e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void IfTheLoopDoesDieItSaysSoAtTheTimeAndAgainAtTeardown()
    {
        // Reaching the last-resort arm takes both guards failing, so both loggers are made to fail:
        // the dispatcher's Warning arm throws, and then the call-site guard's own LogError throws too.
        // What is being pinned is that the outcome is LOUD. Before this bead the identical sequence
        // ended the thread in complete silence, and the only thing teardown said was that the queue
        // had not drained "within 2s" — blaming a slow dispatch for a thread that had been gone for
        // minutes.
        var logger = new FailsOnceLogger(LogLevel.Warning, LogLevel.Error);
        var mock = new Mock<IInputSimulationService>();
        mock.Setup(x => x.MouseDown(It.IsAny<int>())).Throws<ArgumentOutOfRangeException>();

        var handler = new RemoteDesktopHandler(
            logger,
            Mock.Of<IScreenCaptureService>(),
            mock.Object,
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

        handler.EnqueuePointerSampleAsInputEvent(Sample(PointerPhase.ContactStart));

        try
        {
            // Poll rather than sleep a fixed amount: the thread has to have reached the Critical
            // before Dispose is asked what happened, and a fixed wait would be either flaky or slow.
            // Stopwatch rather than wall-clock time so a clock adjustment mid-run cannot end the wait
            // early or extend it indefinitely.
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < LivenessBudget &&
                   !logger.Snapshot().Exists(e => e.Level == LogLevel.Critical))
            {
                Thread.Sleep(10);
            }

            Assert.Contains(logger.Snapshot(), e => e.Level == LogLevel.Critical);
        }
        finally
        {
            // In a finally because the assertion above can fail, and this handler owns a real thread
            // and a BlockingCollection. Leaking those out of a failing test would make whatever runs
            // next look flaky.
            handler.Dispose();
        }

        // The teardown half, which is the part that used to be actively misleading. A thread that
        // caught its way out completes its task NORMALLY, so Task.Wait returns true and nothing here
        // would have anything to report — hence the flag.
        Assert.Contains(
            logger.Snapshot(),
            e => e.Level == LogLevel.Error && e.Message.Contains("had already died during this session"));
    }

    private static void Verify(Mock<ILogger<RemoteDesktopHandler>> logger, LogLevel level, Times times) =>
        logger.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);
}
