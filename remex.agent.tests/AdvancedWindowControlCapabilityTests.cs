using System.Linq;
using Moq;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the advertised <c>SupportsAdvancedWindowControl</c> capability agrees with the window
/// control backend this host actually has (RemEx-is52).
///
/// THE BUG THIS GUARDS WAS PURE DRIFT BETWEEN TWO TRUE STATEMENTS. A complete Win32 implementation
/// exists — <c>WindowsDesktopWindowControlService</c>, doing list/activate/raise/minimize/close/
/// resize — and <c>HostBootstrapper</c> registers it for <c>IDesktopWindowControlService</c> on
/// Windows, overriding the unsupported default. <c>RemoteDesktopHandler</c> dispatches
/// <c>desktop_window_query</c> straight to it with no capability check of its own, so the host would
/// have answered those queries correctly all along. But the capability advertisement opened with
/// <c>if (!OperatingSystem.IsLinux() ...) return false</c> and was never revisited when that backend
/// landed — and the Android client hides its ENTIRE window-control section on that flag. A working,
/// registered, dispatched feature was therefore invisible on the platform it was written for, and an
/// investigation concluded the UI was at fault before finding the host side.
///
/// Nothing failed. Nothing logged. The two halves simply disagreed, which is precisely the shape a
/// test has to hold together, because reading either half alone leaves you convinced it is correct.
/// </summary>
public sealed class AdvancedWindowControlCapabilityTests
{
    private static HostCapabilities Capabilities()
    {
        var provider = new HostCapabilitiesProvider(
            new FakeScreenCaptureService(),
            Mock.Of<IInputSimulationService>());

        return provider.GetCurrent();
    }

    [WindowsOnlyFact("asserts the Windows branch of the capability against the Win32 window backend")]
    public void WindowsAdvertisesAdvancedWindowControlInAnInteractiveSession()
    {
        var capabilities = Capabilities();

        // The test runner is an interactive session, so the only thing that could make this false is
        // the platform check that used to exclude Windows outright.
        Assert.True(
            capabilities.IsInteractiveSession,
            "the test host must be an interactive session for this assertion to mean anything");
        Assert.True(
            capabilities.SupportsAdvancedWindowControl,
            "Windows has a registered Win32 window-control backend, so withholding the capability " +
            "hides a working feature in the client for no reason");
    }

    [WindowsOnlyFact("constructs the Win32 window-control backend and calls into it")]
    public void TheWindowsBackendCanActuallyEnumerateWindows()
    {
        // The other half. Advertising a capability the host cannot deliver would be the same defect
        // with the sign flipped, so this proves the advertisement is not vapour: the very service
        // HostBootstrapper registers on Windows answers a real query.
        var service = new WindowsDesktopWindowControlService();

        var result = service.QueryWindows(new DesktopWindowQuery { Limit = 5 });

        Assert.True(result.Success, result.ErrorText ?? "QueryWindows reported failure");

        // Not asserting a count: a headless or freshly-booted agent may genuinely have no matching
        // top-level windows, and a test that demands one would fail for the wrong reason. Success
        // plus a well-formed list is the honest bar.
        Assert.NotNull(result.Windows);
        Assert.All(result.Windows!, w => Assert.False(string.IsNullOrEmpty(w.Id)));
    }

    [Fact]
    public void VirtualDesktopMovesAreTheOnlyWindowsGapAndFailNonFatally()
    {
        // Recorded because it is the reason the capability LOOKED unsupportable on Windows. The
        // virtual-desktop COM API is undocumented, so that one action is refused — but it is refused
        // with a clear error rather than an exception, which makes it a property of the action, not
        // grounds for withholding list/activate/raise/minimize/close/resize as well.
        if (!System.OperatingSystem.IsWindows()) return;

        var service = new WindowsDesktopWindowControlService();

        var result = service.ExecuteAction(new DesktopWindowAction
        {
            Action = "move_to_desktop",
            WindowId = "0",
            DesktopNumber = 2,
        });

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorText));
    }
}
