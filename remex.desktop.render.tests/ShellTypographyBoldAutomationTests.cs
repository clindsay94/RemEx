using System;
using System.Collections.Generic;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using Remex.Core.Models;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// RemEx-jt6w5.11 regression: flipping Body bold used to attach a runtime
/// <see cref="Avalonia.Styling.Style"/> to <c>Application.Styles</c>, which broke UI Automation's
/// <c>FindAll</c> on the shell root ("Unexpected HRESULT ... COM component") until the process
/// restarted. The fix moved untagged bold to a resource key
/// (<see cref="Remex.Desktop.Services.TypographyResolver.UntaggedBoldFontWeightKey"/>) that the
/// default <c>{x:Type TextBlock}</c> ControlTheme reads via <c>DynamicResource</c> — no
/// <c>Application.Styles</c> mutation at all. This test proves the shell's automation tree can
/// still be walked, without throwing, after Body bold is toggled on and back off.
/// </summary>
public sealed class ShellTypographyBoldAutomationTests
{
    [AvaloniaFact]
    public async System.Threading.Tasks.Task TogglingBodyBold_DoesNotBreakAutomationPeerWalk()
    {
        using var fixture = await ShellRenderFixture.CreateAsync();
        fixture.CaptureFrame();

        // Baseline: the automation tree walks cleanly before anything is toggled.
        WalkAutomationTree(fixture.Window);

        // Flip Body bold ON — the path RemEx-jt6w5.11 repro'd against.
        fixture.Theme.Typography.Apply(
            new TypographySettings { BodyBold = true }, Remex.Desktop.Services.TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window);

        // And back off — the repro said turning it off did NOT recover a broken tree; here nothing
        // should ever have broken.
        fixture.Theme.Typography.Apply(TypographySettings.Default, Remex.Desktop.Services.TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window);
    }

    private static void WalkAutomationTree(Window window)
    {
        var root = ControlAutomationPeer.CreatePeerForElement(window);
        var seen = new HashSet<AutomationPeer>();
        Walk(root, seen);
    }

    private static void Walk(AutomationPeer peer, HashSet<AutomationPeer> seen)
    {
        if (!seen.Add(peer)) return;

        IReadOnlyList<AutomationPeer> children = Array.Empty<AutomationPeer>();
        Action act = () => children = peer.GetChildren();
        act.Should().NotThrow("UI Automation must be able to enumerate the shell's peers regardless of Bold body state");

        foreach (var child in children)
            Walk(child, seen);
    }
}
