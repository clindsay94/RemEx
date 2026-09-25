using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Session;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// PERF-TRACKER P1-10: the keep-awake hold must be released no matter which thread the last
/// disengage runs on. RemoteDesktopHandler engages, awaits the whole stream, then disengages in a
/// finally - so the two calls land on different thread-pool workers. The old
/// <c>SetThreadExecutionState</c> hold was per calling thread, so the release on the second thread
/// cleared nothing and the system stayed awake past the session.
/// </summary>
/// <remarks>
/// Real power state is not observable from a unit test without admin (<c>powercfg /requests</c>) and
/// is shared with every other process on the box, so the guard's ref-counting is tested through the
/// <see cref="IKeepAwakeBackend"/> seam, and the real backend separately proves its handle-based hold
/// is taken on one OS thread and released from another without error.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsInteractiveSessionGuardTests
{
    [WindowsOnlyFact("WindowsInteractiveSessionGuard is a Windows-only type")]
    public void Engage_and_disengage_on_different_threads_releases_the_hold()
    {
        var backend = new CountingBackend();
        var guard = new WindowsInteractiveSessionGuard(NullLogger<WindowsInteractiveSessionGuard>.Instance, backend);

        int engageThread = RunOnNewThread(() => guard.EngageForRemoteControl("client-a"));
        Assert.Equal(1, backend.Active);

        int disengageThread = RunOnNewThread(() => guard.Disengage("client-a"));

        Assert.NotEqual(engageThread, disengageThread);
        Assert.Equal(0, backend.Active);
        Assert.Equal(1, backend.Acquired);
        Assert.Equal(engageThread, backend.AcquireThreads.Single());
        Assert.Equal(disengageThread, backend.ReleaseThreads.Single());
    }

    [WindowsOnlyFact("WindowsInteractiveSessionGuard is a Windows-only type")]
    public void Hold_survives_until_the_last_client_disengages_across_threads()
    {
        var backend = new CountingBackend();
        var guard = new WindowsInteractiveSessionGuard(NullLogger<WindowsInteractiveSessionGuard>.Instance, backend);

        RunOnNewThread(() => guard.EngageForRemoteControl("client-a"));
        RunOnNewThread(() => guard.EngageForRemoteControl("client-b"));
        RunOnNewThread(() => guard.Disengage("client-a"));
        Assert.Equal(1, backend.Active);

        RunOnNewThread(() => guard.Disengage("client-b"));
        Assert.Equal(0, backend.Active);
        Assert.Equal(1, backend.Acquired);
    }

    [WindowsOnlyFact("WindowsInteractiveSessionGuard is a Windows-only type")]
    public void Concurrent_engages_take_one_hold_and_concurrent_disengages_release_it_once()
    {
        const int clients = 32;
        var backend = new CountingBackend();
        var guard = new WindowsInteractiveSessionGuard(NullLogger<WindowsInteractiveSessionGuard>.Instance, backend);

        RunConcurrently(clients, i => guard.EngageForRemoteControl($"client-{i}"));
        Assert.Equal(1, backend.Acquired);
        Assert.Equal(1, backend.Active);

        RunConcurrently(clients, i => guard.Disengage($"client-{i}"));
        Assert.Equal(0, backend.Active);
        Assert.Equal(1, backend.Released);

        // A later session takes a fresh hold rather than reusing the released one.
        guard.EngageForRemoteControl("client-late");
        Assert.Equal(2, backend.Acquired);
        guard.Disengage("client-late");
        Assert.Equal(0, backend.Active);
    }

    [WindowsOnlyFact("WindowsInteractiveSessionGuard is a Windows-only type")]
    public void A_failed_acquire_is_swallowed_and_the_next_session_retries()
    {
        var backend = new CountingBackend { FailNextAcquire = true };
        var guard = new WindowsInteractiveSessionGuard(NullLogger<WindowsInteractiveSessionGuard>.Instance, backend);

        guard.EngageForRemoteControl("client-a");
        Assert.Equal(0, backend.Active);
        guard.Disengage("client-a");
        Assert.Equal(0, backend.Released);

        guard.EngageForRemoteControl("client-b");
        Assert.Equal(1, backend.Active);
        guard.Disengage("client-b");
        Assert.Equal(0, backend.Active);
    }

    [WindowsOnlyFact("The power request API is Win32")]
    public void Real_power_request_is_taken_on_one_thread_and_released_from_another()
    {
        // Exercises the actual P/Invoke signatures (PowerCreateRequest / PowerSetRequest /
        // PowerClearRequest / CloseHandle): any of them failing throws Win32Exception here. The
        // acquiring thread is kept ALIVE (blocked on an event) while the release runs on a second,
        // still-live thread - matching the real bug shape (two live thread-pool workers), not the
        // "first thread already exited" shape a naive RunOnNewThread-then-RunOnNewThread sequence
        // would produce. That distinction matters: CoreCLR can reuse a *dead* thread's
        // ManagedThreadId once its Thread object is finalized (measured ~1 reuse per 200 runs under
        // GC pressure), which both risks a flaky Assert.NotEqual on the IDs and - more importantly -
        // would not actually reproduce the old SetThreadExecutionState bug, since Windows drops a
        // thread's execution-state requirement when that thread exits. A live acquiring thread is
        // the only way this test's shape matches what made the original bug real.
        var backend = new PowerRequestKeepAwakeBackend();
        IDisposable? hold = null;
        var acquired = new ManualResetEventSlim(false);
        var releaseNow = new ManualResetEventSlim(false);
        int acquireThread = 0;

        var acquireT = new Thread(() =>
        {
            acquireThread = Environment.CurrentManagedThreadId;
            hold = backend.Acquire();
            acquired.Set();
            releaseNow.Wait(); // stay alive until the release thread is done with it
        });
        acquireT.Start();
        acquired.Wait();
        Assert.NotNull(hold);

        int releaseThread = RunOnNewThread(() => hold!.Dispose());
        releaseNow.Set();
        acquireT.Join();

        Assert.NotEqual(acquireThread, releaseThread);

        // Idempotent: a second release is a no-op, not a double CloseHandle.
        hold!.Dispose();
    }

    private static int RunOnNewThread(Action action)
    {
        int threadId = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("Action on the worker thread failed.", failure);
        }

        return threadId;
    }

    private static void RunConcurrently(int count, Action<int> action)
    {
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, count)
            .Select(i => new Thread(() => { start.Wait(); action(i); }))
            .ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join());
    }

    /// <summary>Thread-agnostic fake hold that counts acquires/releases and records their threads.</summary>
    private sealed class CountingBackend : IKeepAwakeBackend
    {
        private int _active;
        private int _acquired;
        private int _released;

        public bool FailNextAcquire { get; set; }
        public int Active => Volatile.Read(ref _active);
        public int Acquired => Volatile.Read(ref _acquired);
        public int Released => Volatile.Read(ref _released);
        public List<int> AcquireThreads { get; } = new();
        public List<int> ReleaseThreads { get; } = new();

        public IDisposable Acquire()
        {
            if (FailNextAcquire)
            {
                FailNextAcquire = false;
                throw new InvalidOperationException("simulated PowerCreateRequest failure");
            }

            Interlocked.Increment(ref _acquired);
            Interlocked.Increment(ref _active);
            lock (AcquireThreads) { AcquireThreads.Add(Environment.CurrentManagedThreadId); }
            return new FakeHold(this);
        }

        private sealed class FakeHold(CountingBackend owner) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                Interlocked.Increment(ref owner._released);
                Interlocked.Decrement(ref owner._active);
                lock (owner.ReleaseThreads) { owner.ReleaseThreads.Add(Environment.CurrentManagedThreadId); }
            }
        }
    }
}
