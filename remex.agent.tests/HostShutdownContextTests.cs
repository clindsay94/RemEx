namespace Remex.Agent.Tests;

/// <summary>
/// RemEx-e3pn. Pins the one property that keeps an unhandled UI-thread exception from turning into
/// an unkillable process: the embedded host's shutdown must not block on a continuation that needs
/// a <see cref="SynchronizationContext"/> nobody is pumping any more.
/// </summary>
/// <remarks>
/// Why this is not an Avalonia test: the failure has nothing to do with Avalonia specifically. It
/// needs a SynchronizationContext that accepts posts and never runs them, which is what a dispatcher
/// becomes while its loop is being torn down — and which is eight lines to write here. RemEx-r8c6
/// records that there is no Avalonia headless harness, so a test that needed one would not exist at
/// all, and this defect shipped precisely because nothing exercised the path.
/// </remarks>
public class HostShutdownContextTests
{
    /// <summary>
    /// Accepts posted continuations and never runs them. This is a dispatcher whose message loop is
    /// gone: still installed on the thread, permanently unable to make progress.
    /// </summary>
    private sealed class DeadSynchronizationContext : SynchronizationContext
    {
        private int _posted;

        public int Posted => Volatile.Read(ref _posted);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posted);
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh thread with <paramref name="context"/> installed, and
    /// joins with a bound. The bound matters: if the code under test genuinely deadlocks, this fails
    /// the test instead of wedging the whole suite.
    /// </summary>
    private static T OnThreadWith<T>(SynchronizationContext context, Func<T> body)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the worker thread never finished — the code under test deadlocked outright.");

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }

    /// <summary>An await that posts its continuation to whatever context is current.</summary>
    private static async Task YieldThenComplete() => await Task.Yield();

    [Fact]
    public void BlockingDirectlyOnAContinuationDeadlocksUnderADeadContext()
    {
        // The shape of the ORIGINAL bug, kept as an executable description of it. This is what
        // Program.Main's finally used to do: start async work on a thread carrying a dispatcher
        // context, then block that same thread waiting for it. The continuation is posted back to
        // the blocked thread, so neither side can move.
        var context = new DeadSynchronizationContext();

        var completed = OnThreadWith(context, () => YieldThenComplete().Wait(TimeSpan.FromSeconds(2)));

        Assert.False(completed);
        Assert.True(context.Posted > 0, "the continuation should have been posted to the dead context.");
    }

    [Fact]
    public void RunOffCapturedContextCompletesUnderADeadContext()
    {
        // The fix: the work runs on the thread pool, which carries no SynchronizationContext, so its
        // continuations never need the wedged thread.
        var context = new DeadSynchronizationContext();

        var completed = OnThreadWith(
            context,
            () => Program.RunOffCapturedContext(YieldThenComplete, TimeSpan.FromSeconds(20)));

        Assert.True(completed);
        Assert.Equal(0, context.Posted);
    }

    [Fact]
    public void RunOffCapturedContextGivesUpWhenTheWorkOutlastsTheTimeout()
    {
        // A hosted service that wedges for some unrelated reason must degrade to a slower exit, never
        // to an unbounded one. Without this, the fix above would still hang on a stuck StopAsync.
        var completed = Program.RunOffCapturedContext(
            () => Task.Delay(TimeSpan.FromSeconds(30)),
            TimeSpan.FromMilliseconds(250));

        Assert.False(completed);
    }

    [Fact]
    public void RunOffCapturedContextDoesNotThrowWhenTheWorkFaults()
    {
        // The safety property that matters most. This runs inside Main's finally, which executes
        // while an exception is being unwound — anything thrown here REPLACES that exception, and a
        // diagnosable NullReferenceException becomes a confusing secondary failure with the real
        // cause gone. A faulted task counts as completed: it finished, it just finished badly.
        var completed = Program.RunOffCapturedContext(
            () => Task.FromException(new InvalidOperationException("shutdown blew up")),
            TimeSpan.FromSeconds(5));

        Assert.True(completed);
    }

    [Fact]
    public void RunOffCapturedContextReportsCompletionForWorkThatSucceedsImmediately()
    {
        var completed = Program.RunOffCapturedContext(() => Task.CompletedTask, TimeSpan.FromSeconds(5));

        Assert.True(completed);
    }

    [Fact]
    public void RunOffCapturedContextDoesNotThrowOnANullDelegate()
    {
        // Found in review. A null delegate is a programming error, but throwing for it would defeat
        // the no-throw guarantee this method exists to provide: the throw would escape the unwinding
        // finally in Main and replace the exception being unwound — reintroducing the very failure
        // class the method was written to prevent, through the method written to prevent it.
        var completed = Program.RunOffCapturedContext(null!, TimeSpan.FromSeconds(5));

        Assert.True(completed);
    }
}
