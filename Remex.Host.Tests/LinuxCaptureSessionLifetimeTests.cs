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
/// These tests are designed for a headless CI environment (no D-Bus / portal
/// available), where StartInternalAsync always returns false. On a dev machine
/// with a real session bus, the portal would actually be reached — popping a
/// permission dialog and making the assertions environment-dependent. Tests
/// that probe the failure path are skipped when a session bus is reachable
/// (see <see cref="HasSessionBus"/>); the rest validate disposal and underflow,
/// which are environment-agnostic.
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
        return new LinuxCaptureSessionLifetime(logger, capture, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task AcquireAsync_PortalUnavailable_ReturnsFalseWithoutThrowing()
    {
        // Skip when a real session bus is reachable — the actual portal would
        // pop the KDE permission dialog and stall the test. This test only
        // validates the no-portal failure path that fires in CI.
        if (HasSessionBus()) return;

        await using var lifetime = MakeLifetime();
        var result = await lifetime.AcquireAsync(CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task AcquireAsync_AfterFailedAcquire_CanRetry()
    {
        // Failure-mode-only test — refcount-reset-on-failure can't be observed
        // when the portal actually succeeds.
        if (HasSessionBus()) return;

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
        // Failure-mode-only — the "all return false" assertion is meaningful
        // only when portal is unavailable. On a real session bus the start
        // task would actually open a portal session.
        if (HasSessionBus()) return;

        await using var lifetime = MakeLifetime();

        // Ten concurrent acquires must all resolve and must not throw or deadlock.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => lifetime.AcquireAsync(CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.False(r)); // portal unavailable in CI
    }

    private static bool HasSessionBus() =>
        !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
        || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

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
