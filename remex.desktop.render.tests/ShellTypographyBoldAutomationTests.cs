using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Core.Models;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// RemEx-jt6w5.11 background: flipping Body bold used to attach a runtime
/// <see cref="Avalonia.Styling.Style"/> to <c>Application.Styles</c>, and a real UI Automation
/// client's <c>FindAll</c> on the shell root then threw "Unexpected HRESULT has been returned from
/// a call to a COM component" until the process restarted. The fix moved untagged bold to a
/// resource key (<see cref="Remex.Desktop.Services.TypographyResolver.UntaggedBoldFontWeightKey"/>)
/// that the default <c>{x:Type TextBlock}</c> ControlTheme reads via <c>DynamicResource</c> — no
/// <c>Application.Styles</c> mutation at all.
/// </summary>
/// <remarks>
/// WHAT THIS TEST DOES NOT PROVE: it cannot reproduce the original COM fault. Verified by running
/// a peer-walk-only version of this test in a worktree at 659d9660 (the commit the fault was
/// measured against, before this fix): it PASSED there too. Avalonia's headless
/// <see cref="AutomationPeer.GetChildren"/> walk never crosses into the Win32
/// <c>System.Windows.Automation</c> / <c>AutomationNode</c> COM layer where "Unexpected HRESULT"
/// actually originated, so a throw here was never going to correlate with that fault either way.
/// WHAT THIS TEST DOES PROVE, which is the closest an automated, non-Windows-UIA test can get:
/// (1) the shell's own <see cref="AutomationPeer"/> tree is walkable, with a floor on peers visited
/// so the walk can't pass vacuously on an empty tree, in all three states — before touching Bold
/// body, with it on, and with it off again; and (2) a real, untagged button content TextBlock's
/// INHERITED weight (measured in the actual shell: the candidates found are Regular, not the
/// "Medium" the design comments assumed Material buttons inherit — see
/// <see cref="FindNonBoldButtonTextBlock"/>) survives Bold body OFF, goes Bold while it is ON, and
/// is restored — not forced to some other weight — once it is OFF again. The actual "does UI
/// Automation still work" claim is verified manually against the real Windows UIA stack; see the
/// task report for that probe's before/after output.
/// </remarks>
public sealed class ShellTypographyBoldAutomationTests
{
    [AvaloniaFact]
    public async System.Threading.Tasks.Task TogglingBodyBold_DoesNotBreakAutomationPeerWalk()
    {
        using var fixture = await ShellRenderFixture.CreateAsync();
        fixture.CaptureFrame();

        // A real Button in the real shell whose content TextBlock is untagged (Theme null) and NOT
        // already Bold from one of RemEx's own button classes (primary/secondary/... set FontWeight
        // explicitly — measured: the untagged candidates in this shell are Regular, not the "Medium"
        // the design comments assumed Material buttons inherit, so this asserts against the button's
        // OWN measured baseline rather than hard-coding an assumption the real tree contradicts).
        // This is the one property the old runtime Style's page-title/card-title exclusion existed
        // to protect, and the one thing a DynamicResource that resolves to Unset must not disturb.
        WalkAutomationTree(fixture.Window, out var baselineSeen);
        baselineSeen.Should().BeGreaterThan(20,
            "a near-empty peer tree would let this test pass vacuously even if walking real content threw");
        var buttonTextBlock = FindNonBoldButtonTextBlock(fixture.Window);
        var inheritedWeight = buttonTextBlock.FontWeight;
        inheritedWeight.Should().NotBe(FontWeight.Bold, "the baseline must not already be Bold or the assertions below would be meaningless");

        // Flip Body bold ON — the path RemEx-jt6w5.11 repro'd against.
        fixture.Theme.Typography.Apply(
            new TypographySettings { BodyBold = true }, Remex.Desktop.Services.TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window, out var boldSeen);
        boldSeen.Should().BeGreaterThan(20);
        Avalonia.Application.Current!.TryGetResource("Typo.UntaggedBold.FontWeight", null, out var res).Should().BeTrue("the key must be present in the merged dictionary once Body bold is on");
        res.Should().Be(FontWeight.Bold);
        buttonTextBlock.FontWeight.Should().Be(FontWeight.Bold,
            "the same button's content TextBlock is untagged, so the DynamicResource setter reaches it too — this is not somehow exempt");

        // And back off — the repro said turning it off did NOT recover a broken tree; here nothing
        // should ever have broken.
        fixture.Theme.Typography.Apply(TypographySettings.Default, Remex.Desktop.Services.TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window, out var restoredSeen);
        restoredSeen.Should().BeGreaterThan(20);
        buttonTextBlock.FontWeight.Should().Be(inheritedWeight,
            "Bold body OFF must restore the button's inherited weight, not leave it Bold or force some other value");
    }

    /// <summary>
    /// The first untagged (<c>Theme == null</c>) <see cref="TextBlock"/> inside a real shell
    /// <see cref="Button"/> whose baseline weight is not already <see cref="FontWeight.Bold"/> —
    /// RemEx's own button classes (primary/secondary/...) set FontWeight explicitly, so this filters
    /// those out and keeps only a button relying on inheritance, the case Bold body must not disturb
    /// when off and must actually change when on.
    /// </summary>
    private static TextBlock FindNonBoldButtonTextBlock(Visual root)
    {
        var candidates = root.GetVisualDescendants().OfType<Button>()
            .SelectMany(button => button.GetVisualDescendants().OfType<TextBlock>())
            .Where(tb => tb.Theme is null)
            .ToArray();

        var textBlock = candidates.FirstOrDefault(tb => tb.FontWeight != FontWeight.Bold);

        textBlock.Should().NotBeNull(
            $"the shell must have a Button with an untagged, non-Bold content TextBlock to make this assertion meaningful -- found {candidates.Length} untagged candidate(s): {string.Join(", ", candidates.Select(c => $"'{c.Text}'={c.FontWeight}"))}");
        return textBlock!;
    }

    private static void WalkAutomationTree(Window window, out int peerCount)
    {
        var root = ControlAutomationPeer.CreatePeerForElement(window);
        var seen = new HashSet<AutomationPeer>();
        Walk(root, seen);
        peerCount = seen.Count;
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
