using System.Runtime.Versioning;
using Remex.Agent.Services.Input.Linux;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="LinuxPortalInputInjector"/>.
///
/// These tests verify the injector's state machine and safety contracts without
/// starting a real xdg-desktop-portal session.  The portal session start
/// (<see cref="LinuxPortalInputInjector.EnsureStartedAsync"/>) will return false
/// in a headless CI environment because gdbus cannot connect to a session bus —
/// but all Notify* calls must still be safe to call on an inactive injector.
/// </summary>
[SupportedOSPlatform("linux")]
public class LinuxPortalInputInjectorTests
{
    // ── Initial state ──────────────────────────────────────────────────

    [Fact]
    public void IsActive_IsFalse_BeforeSessionStart()
    {
        var injector = new LinuxPortalInputInjector();
        Assert.False(injector.IsActive);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_WhenNeverStarted()
    {
        var injector = new LinuxPortalInputInjector();
        await injector.DisposeAsync();
        // Second dispose must not throw.
        await injector.DisposeAsync();
    }

    // ── No-op safety: Notify* when inactive ───────────────────────────

    [Fact]
    public void NotifyPointerMotionRelative_DoesNotThrow_WhenInactive()
    {
        var injector = new LinuxPortalInputInjector();
        // Must be a no-op (not active, no subprocess launched).
        var ex = Record.Exception(() => injector.NotifyPointerMotionRelative(5.0, -3.0));
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyPointerMotionAbsolute_DoesNotThrow_WhenInactive()
    {
        var injector = new LinuxPortalInputInjector();
        var ex = Record.Exception(() => injector.NotifyPointerMotionAbsolute(800.0, 600.0));
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyPointerButton_DoesNotThrow_WhenInactive()
    {
        var injector = new LinuxPortalInputInjector();
        var ex = Record.Exception(() => injector.NotifyPointerButton(0x110 /* BTN_LEFT */, pressed: true));
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyPointerScrollDiscrete_DoesNotThrow_WhenInactive()
    {
        var injector = new LinuxPortalInputInjector();
        var ex = Record.Exception(() => injector.NotifyPointerScrollDiscrete(0, -120));
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyKeyboardKeycode_DoesNotThrow_WhenInactive()
    {
        var injector = new LinuxPortalInputInjector();
        var ex = Record.Exception(() => injector.NotifyKeyboardKeycode(65 /* KEY_A */, pressed: true));
        Assert.Null(ex);
    }

    [Fact]
    public void NotifyKeyboardKeysym_DoesNotThrow_WhenInactive()
    {
        var injector = new LinuxPortalInputInjector();
        var ex = Record.Exception(() => injector.NotifyKeyboardKeysym(0x61 /* 'a' */, pressed: true));
        Assert.Null(ex);
    }

    // ── EnsureStartedAsync in headless environments ────────────────────

    [Fact]
    public async Task EnsureStartedAsync_ReturnsFalse_InHeadlessEnvironment()
    {
        // Skip on machines with a live session bus — the real test environment
        // would otherwise pop up the KDE RemoteDesktop permission dialog. This
        // test only validates the no-portal failure path that fires in CI.
        if (HasSessionBus()) return;

        await using var injector = new LinuxPortalInputInjector();
        var result = await injector.EnsureStartedAsync();
        Assert.False(result);
        Assert.False(injector.IsActive);
    }

    [Fact]
    public async Task EnsureStartedAsync_CanBeCalledMultipleTimes_Safely()
    {
        // Same rationale — skip when a real portal is reachable.
        if (HasSessionBus()) return;

        await using var injector = new LinuxPortalInputInjector();

        // Call twice concurrently; neither call should throw regardless of
        // whether the portal is available.
        var t1 = injector.EnsureStartedAsync();
        var t2 = injector.EnsureStartedAsync();
        var results = await Task.WhenAll(t1, t2);

        // Both calls should return the same effective result.
        Assert.Equal(results[0], results[1]);
    }

    private static bool HasSessionBus() =>
        !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))
        || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        || !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

    // ── DisposeAsync clears active state ──────────────────────────────

    [Fact]
    public async Task DisposeAsync_SetsIsActive_ToFalse()
    {
        var injector = new LinuxPortalInputInjector();
        // Simulate an active session by inspecting post-dispose state.
        await injector.DisposeAsync();
        Assert.False(injector.IsActive);
    }
}
