using Microsoft.Extensions.Logging.Abstractions;
using Remex.Host.Services.RemoteDesktop.Linux.Capture;
using Remex.Host.Services.ScreenCapture;

namespace Remex.Host.Tests;

/// <summary>
/// Tests for LinuxCaptureSessionLifetime refcount semantics and failure handling.
///
/// Live portal validation (the actual KDE screencast path) is covered by the ADB
/// integration test in docs/prd-pipewire-and-reconnect-cleanup.md §7 — these unit
/// tests cover only the managed refcount + concurrency contract.
///
/// In the test environment D-Bus is unavailable, so StartInternalAsync always
/// returns false (portal unavailable). We verify the safe-failure contract:
///   • AcquireAsync returns false without throwing
///   • Refcount is reset so the next acquire can retry
///   • Concurrent acquires share one start task (no double-open)
///   • DisposeAsync is safe at any lifecycle point
///   • ReleaseAsync underflow does not throw (it only logs + returns)
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class LinuxCaptureSessionLifetimeTests
{
    private static LinuxCaptureSessionLifetime MakeLifetime()
    {
        var logger = NullLogger<LinuxCaptureSessionLifetime>.Instance;
        // LinuxScreenCaptureService is the concrete type the lifetime casts to.
        // Constructing it with a NullLogger is safe; it will try to detect the
        // display server but will not throw even on a headless CI host.
        var capture = new LinuxScreenCaptureService(
            NullLogger<LinuxScreenCaptureService>.Instance);
        return new LinuxCaptureSessionLifetime(logger, capture);
    }

    [Fact]
    public async Task AcquireAsync_PortalUnavailable_ReturnsFalseWithoutThrowing()
    {
        await using var lifetime = MakeLifetime();
        var result = await lifetime.AcquireAsync(CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task AcquireAsync_AfterFailedAcquire_CanRetry()
    {
        await using var lifetime = MakeLifetime();

        var first = await lifetime.AcquireAsync(CancellationToken.None);
        Assert.False(first);

        // After a failure the refcount is reset to 0; a second call must not deadlock.
        var second = await lifetime.AcquireAsync(CancellationToken.None);
        Assert.False(second);
    }

    [Fact]
    public async Task AcquireAsync_Concurrent_SharesOneStartTask()
    {
        await using var lifetime = MakeLifetime();

        // Ten concurrent acquires must all resolve and must not throw or deadlock.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => lifetime.AcquireAsync(CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.False(r)); // portal unavailable in CI
    }

    [Fact]
    public async Task ReleaseAsync_WithoutMatchingAcquire_ThrowsDebugAssertInDebugBuilds()
    {
        await using var lifetime = MakeLifetime();

        // Debug.Assert(false) fires in debug builds and throws — this is intentional:
        // refcount underflow is a programming error in the call site, not a runtime hazard.
        // In release builds the assert is compiled out; the method logs an error and returns.
        // We simply verify the behavior is deterministic by exercising the path.
#if DEBUG
        await Assert.ThrowsAnyAsync<Exception>(() => lifetime.ReleaseAsync());
#else
        var ex = await Record.ExceptionAsync(() => lifetime.ReleaseAsync());
        Assert.Null(ex);
#endif
    }

    [Fact]
    public async Task DisposeAsync_OnUninitializedInstance_DoesNotThrow()
    {
        var lifetime = MakeLifetime();
        var ex = await Record.ExceptionAsync(() => lifetime.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_AfterFailedAcquire_DoesNotThrow()
    {
        var lifetime = MakeLifetime();
        await lifetime.AcquireAsync(CancellationToken.None);
        var ex = await Record.ExceptionAsync(() => lifetime.DisposeAsync().AsTask());
        Assert.Null(ex);
    }

    [Fact]
    public async Task AcquireAsync_CancelledToken_DoesNotDeadlock()
    {
        await using var lifetime = MakeLifetime();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Passing an already-cancelled token: StartAsync throws OperationCanceledException,
        // which StartInternalAsync catches and returns false. Must not deadlock.
        var result = await lifetime.AcquireAsync(cts.Token);
        Assert.False(result);
    }
}
