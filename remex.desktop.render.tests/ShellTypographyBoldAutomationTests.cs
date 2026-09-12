using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Diagnostics;
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
/// a call to a COM component" until the process restarted. The fix (round 2, after a round-1
/// mechanism was measured and found broken) is <see cref="Remex.Desktop.Services.TypographyService"/>
/// setting/clearing an INHERITED <c>TextBlock.FontWeight</c> local value on each top-level window's
/// root when Body bold flips — no <c>Application.Styles</c> mutation, and no resource key at all:
/// nearest-ancestor wins, so a Material button's own FontWeight (Medium/SemiBold, set closer to its
/// label than the window root) is never disturbed, while genuinely untagged text with no
/// weight-setting ancestor inherits Bold.
/// </summary>
/// <remarks>
/// WHAT THIS TEST DOES NOT PROVE: it cannot reproduce the original COM fault. Verified by running
/// a peer-walk-only version of this test in a worktree at 659d9660 (the commit the fault was
/// measured against, before any RemEx-jt6w5.11 fix): it PASSED there too. Avalonia's headless
/// <see cref="AutomationPeer.GetChildren"/> walk never crosses into the Win32
/// <c>System.Windows.Automation</c> / <c>AutomationNode</c> COM layer where "Unexpected HRESULT"
/// actually originated, so a throw here was never going to correlate with that fault either way.
/// WHAT THIS TEST DOES PROVE, which is the closest an automated, non-Windows-UIA test can get:
/// (1) the shell's own <see cref="AutomationPeer"/> tree is walkable, with a floor on peers visited
/// so the walk can't pass vacuously on an empty tree, in all three states — before touching Bold
/// body, with it on, and with it off again; and (2) TWO real targets, pinned by exact weight AND
/// <c>GetDiagnostic</c> priority in all three states. The shell's own <c>ConnectionStatusButton</c>
/// status text (Inherited-priority SemiBold, from the Button's own <c>.secondary</c> class, which
/// holds it at Style priority) stays SemiBold throughout, because the Button itself is a nearer
/// ancestor than the window root and already supplies a value — proving Bold body never disturbs a
/// button that sets its own weight. MEASURED ALONG THE WAY: even an unclassed default
/// <see cref="Button"/> is NOT a valid "genuinely untagged" test target, because Material's own
/// <c>MaterialButtonBase</c> sets <c>FontWeight</c> at STYLE priority directly on the Button control
/// itself (Material.Avalonia 3.19, Button.axaml:22,135-162) — every Button, not just RemEx's own
/// classed ones, already blocks inheritance from reaching its label. So the second target is a bare
/// <see cref="TextBlock"/> with no Button ancestor at all: Unset/Regular at baseline (nothing
/// anywhere sets this property for it), Inherited/Bold while Bold body is ON, and back to
/// Unset/Regular — not forced Regular by a leftover setter, genuinely nothing set — once it is OFF
/// again. The actual "does UI Automation still work" claim is verified manually against the real
/// Windows UIA stack; see the task report for that probe's before/after output. The task report also
/// carries this test's own six <c>GetDiagnostic</c> readings, plus the four earlier readings taken
/// against the ABANDONED round-1 mechanism that caught its bug.
/// </remarks>
public sealed class ShellTypographyBoldAutomationTests
{
    [AvaloniaFact]
    public async System.Threading.Tasks.Task TogglingBodyBold_DoesNotBreakAutomationPeerWalk()
    {
        using var fixture = await ShellRenderFixture.CreateAsync();
        fixture.CaptureFrame();

        // This headless harness never calls StartWithClassicDesktopLifetime (RenderTestApp only
        // arms App.SkipProductionStartup), so Application.Current.ApplicationLifetime is null here
        // unlike production -- TypographyService.ApplyUntaggedBold reaches windows through
        // IClassicDesktopStyleApplicationLifetime.Windows exactly like App.axaml.cs does, so without
        // this the round-2 mechanism would silently no-op in this test for a reason that has nothing
        // to do with the mechanism itself. Wiring up the real lifetime type exercises the real code
        // path instead of asserting against a hand-rolled substitute.
        var lifetime = new Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime
        {
            MainWindow = fixture.Window,
        };
        // Application.ApplicationLifetime's public setter throws "not possible to change... after
        // Application was initialized" -- true in production (set once, from Program.cs), but this
        // per-test headless Application instance (PerTest isolation, RenderTestApp.cs) never had one
        // set at all, so there is nothing to protect here. Reflection is test-only scaffolding to
        // reach the same field App.axaml.cs's production Main sets through the public setter.
        typeof(Avalonia.Application)
            .GetField("_applicationLifetime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(Avalonia.Application.Current, lifetime);
        // ClassicDesktopStyleApplicationLifetime's public Windows list is populated by the AppBuilder
        // wiring StartWithClassicDesktopLifetime installs (a Window.Show() hook), which this manually
        // constructed instance never got -- measured: MainWindow alone does not add it to Windows,
        // and Window.Show() again after installing the lifetime doesn't either. Reach the backing
        // AvaloniaList directly so this test exercises the same Windows collection
        // TypographyService.ApplyUntaggedBold enumerates in production.
        var windowsField = typeof(Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime)
            .GetField("_windows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Avalonia.Collections.AvaloniaList<Window>)windowsField.GetValue(lifetime)!).Add(fixture.Window);
        LogLine($"lifetime.Windows.Count = {lifetime.Windows.Count}");

        // (a) ConnectionStatusButton's StatusText (ShellView.axaml:915,943): Classes="secondary" ->
        // App.axaml's `:is(Button).secondary` sets FontWeight=SemiBold on the Button; StatusText
        // itself carries no FontWeight/Theme, so it inherits SemiBold from its own Button ancestor.
        var connectionButton = (Button)fixture.Window.GetVisualDescendants()
            .Single(c => c is Button b && b.Name == "ConnectionStatusButton");
        var statusText = connectionButton.GetVisualDescendants().OfType<TextBlock>()
            .First(tb => tb.Theme is null && tb.FontSize == 10);

        // (b) A throwaway plain, untagged TextBlock with NO Button ancestor, added into the REAL
        // shell window's visual tree (not a standalone Window — measured in round 1 that a bare
        // `new Window` does not reliably resolve the app's implicit ControlThemes the same way) so
        // it shares the exact same styling context as everything else in this test. This, not a
        // Button's own label, is the genuinely untagged case: measured (see below) that Material's
        // MaterialButtonBase itself sets FontWeight=Medium/SemiBold at STYLE priority on the Button
        // control (Material.Avalonia 3.19, Button.axaml:22,135-162) -- not merely inherited by its
        // content TextBlock from further up -- so ANY Button, even an unclassed one, already blocks
        // inheritance from reaching its own label; a bare TextBlock with no such ancestor is the
        // only way to exercise the "genuinely untagged text inherits the toggle" half of the claim.
        var probeText = new TextBlock { Text = "Probe" };
        var wrapper = new Grid();
        var originalContent = (Control)fixture.Window.Content!;
        fixture.Window.Content = null;
        wrapper.Children.Add(originalContent);
        wrapper.Children.Add(probeText);
        fixture.Window.Content = wrapper;
        Dispatcher.UIThread.RunJobs();
        fixture.CaptureFrame();
        probeText.Theme.Should().BeNull("this probe must be genuinely untagged for the assertions below to mean anything");

        // Baseline.
        WalkAutomationTree(fixture.Window, out var baselineSeen);
        baselineSeen.Should().BeGreaterThan(20,
            "a near-empty peer tree would let this test pass vacuously even if walking real content threw");
        LogDiagnostic("baseline", "ConnectionStatusButton/StatusText", statusText);
        LogDiagnostic("baseline", "bare untagged probe TextBlock", probeText);
        statusText.FontWeight.Should().Be(FontWeight.SemiBold,
            "ConnectionStatusButton's own .secondary class sets SemiBold; Bold body OFF must not disturb it");
        statusText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Inherited,
            "StatusText itself has no local/style value; it inherits SemiBold from its own Button ancestor (which holds it at Style priority)");
        probeText.FontWeight.Should().Be(FontWeight.Normal,
            "with no ancestor supplying FontWeight and nothing set yet, this is the property's own registered default");
        probeText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Unset,
            "nothing anywhere in the chain has ever set this property for this bare probe at baseline");

        // Flip Body bold ON — the path RemEx-jt6w5.11 repro'd against.
        fixture.Theme.Typography.Apply(
            new TypographySettings { BodyBold = true }, Remex.Desktop.Services.TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window, out var boldSeen);
        boldSeen.Should().BeGreaterThan(20);
        LogDiagnostic("Bold body ON", "ConnectionStatusButton/StatusText", statusText);
        LogDiagnostic("Bold body ON", "bare untagged probe TextBlock", probeText);
        statusText.FontWeight.Should().Be(FontWeight.SemiBold,
            "ConnectionStatusButton itself is the nearer ancestor for inheritance purposes and already supplies a value (Style-priority SemiBold), so the window-root Bold set by Bold body ON never reaches this TextBlock — this is the whole point of the inheritance-only mechanism");
        probeText.FontWeight.Should().Be(FontWeight.Bold,
            "the bare probe has no nearer ancestor supplying FontWeight at all, so it inherits Bold from the window root");
        probeText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Inherited,
            "now inherited from the window root's local value, where at baseline nothing supplied a value at all");

        // And back off — the repro said turning it off did NOT recover a broken tree; here nothing
        // should ever have broken.
        fixture.Theme.Typography.Apply(TypographySettings.Default, Remex.Desktop.Services.TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window, out var restoredSeen);
        restoredSeen.Should().BeGreaterThan(20);
        LogDiagnostic("Bold body OFF (restored)", "ConnectionStatusButton/StatusText", statusText);
        LogDiagnostic("Bold body OFF (restored)", "bare untagged probe TextBlock", probeText);
        statusText.FontWeight.Should().Be(FontWeight.SemiBold,
            "unaffected throughout; still the .secondary class's own SemiBold");
        probeText.FontWeight.Should().Be(FontWeight.Normal,
            "Bold body OFF clears the window root's local value entirely (ClearValue), restoring exactly the baseline Unset/default state -- not forced to Regular by a leftover setter, just genuinely nothing set");
        probeText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Unset,
            "back to exactly the baseline state: nothing set anywhere in the chain");
    }

    /// <summary>
    /// Prints the exact <c>Value</c> and <c>Priority</c> <c>GetDiagnostic</c>
    /// reports for <see cref="TextBlock.FontWeightProperty"/> — captured verbatim into the task
    /// report while diagnosing the round-1 mechanism (a DynamicResource-based ControlTheme setter),
    /// kept here so a future regression shows the same evidence trail without re-instrumenting.
    /// </summary>
    private static void LogDiagnostic(string state, string label, TextBlock target)
    {
        var diag = target.GetDiagnostic(TextBlock.FontWeightProperty);
        LogLine($"{state} | {label} | Value={diag.Value} Priority={diag.Priority}");
    }

    // Console.WriteLine is not reliably captured by the xunit v3 VSTest adapter in this harness
    // (measured); a plain file sidesteps that so the readings always reach the task report.
    private static void LogLine(string message)
    {
        var line = $"[jt6w5.11 diagnostic] {message}";
        Console.WriteLine(line);
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jt6w5-diagnostics.log"), line + Environment.NewLine);
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
