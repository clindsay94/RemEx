using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// RemEx-jt6w5.11 background: flipping Body bold used to attach a runtime
/// <see cref="Avalonia.Styling.Style"/> to <c>Application.Styles</c>, and a real UI Automation
/// client's <c>FindAll</c> on the shell root then threw "Unexpected HRESULT has been returned from
/// a call to a COM component" until the process restarted — THE ORIGINAL MECHANISM, reverted.
/// </summary>
/// <remarks>
/// <para>
/// ATTEMPT 1 (never shipped, caught by this test's ancestor before it could): a
/// <c>FontWeight</c> Setter on the default <c>{x:Type TextBlock}</c> ControlTheme bound to a
/// <c>DynamicResource</c> that toggled between <c>FontWeight.Bold</c> and
/// <c>AvaloniaProperty.UnsetValue</c>. Measured (<c>GetDiagnostic</c>) that the Unset value
/// registers a real Style-priority frame resolving to the property's own default (Regular),
/// outranking a Button's own Style-priority FontWeight setter — a forced-Regular regression.
/// </para>
/// <para>
/// ATTEMPT 2 (never shipped): <c>TypographyService</c> pushed the inherited weight directly onto
/// each already-open top-level window's root (<c>IClassicDesktopStyleApplicationLifetime.Windows</c>)
/// at Apply time. Measured broken for a different reason: both startup Apply calls run before
/// <c>MainWindow</c> exists, so a persisted Body-bold-on opened the shell Regular at cold start
/// until the next settings change, and the same gap applied to every on-demand window
/// (<c>TrayFlyoutWindow</c>, <c>PairingDialog</c>, <c>CommandPaletteWindow</c>, etc.) created after
/// the last Apply — this test's own first version hid that bug by injecting its probe window
/// AFTER calling Apply, the opposite of the real gap.
/// </para>
/// <para>
/// THE MECHANISM (current): a STATIC <c>Style Selector="Window"</c> in
/// <c>Styles/Typography.axaml</c>, present from the moment that stylesheet loads (before any window
/// exists), sets the INHERITED attached property <c>TextElement.FontWeight</c> on every Window from
/// a <c>DynamicResource</c> (<c>Typo.UntaggedBold.FontWeight</c>) that <c>TypographyService</c> keeps
/// ALWAYS PRESENT — <c>FontWeight.Bold</c> when Body bold is on, <c>FontWeight.Normal</c> when off,
/// never absent, never <c>AvaloniaProperty.UnsetValue</c>. Because the Style exists before any
/// window does, every window — cold start's <c>MainWindow</c> included, and every on-demand window —
/// gets the CURRENT value from construction; no per-window push, no runtime Styles-collection
/// mutation, no resource key that is ever absent. Nearest-ancestor wins for children: a Material
/// button (even an unclassed one — its own template sets FontWeight at Style priority directly on
/// the Button control, not merely on its content TextBlock, measured below) and RemEx's own button
/// classes are nearer ancestors than the window root, so their labels are unaffected; themed members
/// and <c>.page-title</c>/<c>.card-title</c> carry their own setters.
/// </para>
/// <para>
/// This suite proves three things: (1) <see cref="TogglingBodyBold_DoesNotBreakAutomationPeerWalk"/>
/// — the shell's automation peer tree stays walkable (with a floor on peers visited so the walk
/// can't pass vacuously) across all three Body-bold states, and two real targets keep their exact
/// declared weight through the toggle, pinned by both <c>FontWeight</c> value and
/// <c>GetDiagnostic</c> priority: <c>ConnectionStatusButton</c>'s StatusText (Inherited-priority
/// SemiBold from its own <c>.secondary</c> class the whole time — a nearer ancestor already supplies
/// a value) and a bare untagged <see cref="TextBlock"/> with no Button ancestor at all
/// (Inherited-priority Regular at baseline and once restored, Inherited-priority Bold while on —
/// genuinely inheriting the toggle each time, from an always-present Window-style value that is
/// never absent and never <c>AvaloniaProperty.UnsetValue</c>). (2)
/// <see cref="ColdStart_BodyBoldSetBeforeAnyWindowExists_TheFirstWindowIsAlreadyCorrect"/> — the
/// exact shape Attempt 2 got wrong: <c>Apply</c> runs BEFORE any window is created, then a window is
/// created and shown, and it already carries the right weight from its very first frame, no second
/// Apply needed. The actual "does UI Automation still work" claim is verified manually against the
/// real Windows UIA stack; the task report carries this suite's <c>GetDiagnostic</c> readings.
/// </para>
/// </remarks>
public sealed class ShellTypographyBoldAutomationTests
{
    [AvaloniaFact]
    public async System.Threading.Tasks.Task TogglingBodyBold_DoesNotBreakAutomationPeerWalk()
    {
        using var fixture = await ShellRenderFixture.CreateAsync();
        fixture.CaptureFrame();

        // (a) ConnectionStatusButton's StatusText (ShellView.axaml:915,943): Classes="secondary" ->
        // App.axaml's `:is(Button).secondary` sets FontWeight=SemiBold on the Button; StatusText
        // itself carries no FontWeight/Theme, so it inherits SemiBold from its own Button ancestor.
        var connectionButton = (Button)fixture.Window.GetVisualDescendants()
            .Single(c => c is Button b && b.Name == "ConnectionStatusButton");
        var statusText = connectionButton.GetVisualDescendants().OfType<TextBlock>()
            .First(tb => tb.Theme is null && tb.FontSize == 10);

        // (b) A throwaway plain, untagged TextBlock with NO Button ancestor, added into the REAL
        // shell window's visual tree so it shares the exact same styling context as everything else
        // in this test. This, not a Button's own label, is the genuinely untagged case: measured
        // that Material's own MaterialButtonBase sets FontWeight at STYLE priority directly on the
        // Button control (Material.Avalonia 3.19, Button.axaml:22,135-162) -- not merely inherited
        // by its content TextBlock from further up -- so ANY Button, even an unclassed one, already
        // blocks inheritance from reaching its own label; a bare TextBlock with no such ancestor is
        // the only way to exercise the "genuinely untagged text inherits the toggle" half of the claim.
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
        statusText.FontWeight.Should().Be(FontWeight.SemiBold,
            "ConnectionStatusButton's own .secondary class sets SemiBold; Bold body OFF must not disturb it");
        statusText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Inherited,
            "StatusText itself has no local/style value; it inherits SemiBold from its own Button ancestor (which holds it at Style priority)");
        probeText.FontWeight.Should().Be(FontWeight.Normal,
            "the Window style's resource is Normal at defaults -- an explicit value, always present, not an absent/Unset one");
        probeText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Inherited,
            "inherited from the Window's own Style-priority value, which is always present under this mechanism -- never Unset");

        // Flip Body bold ON — the path RemEx-jt6w5.11 repro'd against.
        fixture.Theme.Typography.Apply(
            new TypographySettings { BodyBold = true }, TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window, out var boldSeen);
        boldSeen.Should().BeGreaterThan(20);
        statusText.FontWeight.Should().Be(FontWeight.SemiBold,
            "ConnectionStatusButton itself is the nearer ancestor for inheritance purposes and already supplies a value (Style-priority SemiBold), so the Window style's Bold never reaches this TextBlock — this is the whole point of the inheritance-based mechanism");
        probeText.FontWeight.Should().Be(FontWeight.Bold,
            "the bare probe has no nearer ancestor supplying FontWeight at all, so it inherits Bold from the Window style");
        probeText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Inherited,
            "now inherited from the Window's own Style-priority value, where at baseline nothing supplied a value at all");

        // And back off — the repro said turning it off did NOT recover a broken tree; here nothing
        // should ever have broken.
        fixture.Theme.Typography.Apply(TypographySettings.Default, TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        WalkAutomationTree(fixture.Window, out var restoredSeen);
        restoredSeen.Should().BeGreaterThan(20);
        statusText.FontWeight.Should().Be(FontWeight.SemiBold,
            "unaffected throughout; still the .secondary class's own SemiBold");
        probeText.FontWeight.Should().Be(FontWeight.Normal,
            "Bold body OFF sets the Window style's resource back to an explicit Normal -- restoring exactly the baseline value, not a leftover Bold");
        probeText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Inherited,
            "still inherited from the Window's Style-priority value, now Normal again");
    }

    /// <summary>
    /// The exact shape Attempt 2 got wrong: <c>Apply(BodyBold = true)</c> runs FIRST, before any
    /// window exists, then a window is created and shown — proving the static Window style reaches
    /// it from its very first frame with no second Apply required, unlike a per-window push which
    /// can only ever reach windows that already exist at Apply time.
    /// </summary>
    [AvaloniaFact]
    public void ColdStart_BodyBoldSetBeforeAnyWindowExists_TheFirstWindowIsAlreadyCorrect()
    {
        var typography = new TypographyService();
        typography.Apply(new TypographySettings { BodyBold = true }, TypographyService.DefaultSurface);

        // Only now does a window get created -- this is the cold-start / on-demand-window shape.
        var probeText = new TextBlock { Text = "Probe" };
        var secondaryButton = new Button { Classes = { "secondary" }, Content = "Status" };
        var window = new Window { Content = new StackPanel { Children = { probeText, secondaryButton } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var buttonLabel = secondaryButton.GetVisualDescendants().OfType<TextBlock>().First(tb => tb.Theme is null);

        probeText.FontWeight.Should().Be(FontWeight.Bold,
            "the window was created AFTER BodyBold=true was applied -- the static Window style must already carry Bold on its very first frame, with no second Apply needed");
        probeText.GetDiagnostic(TextBlock.FontWeightProperty).Priority.Should().Be(Avalonia.Data.BindingPriority.Inherited);
        buttonLabel.FontWeight.Should().Be(FontWeight.SemiBold,
            "the .secondary class's own Style-priority setter on the Button is a nearer ancestor and is unaffected");

        typography.Apply(TypographySettings.Default, TypographyService.DefaultSurface);
        Dispatcher.UIThread.RunJobs();

        probeText.FontWeight.Should().Be(FontWeight.Normal, "off restores the explicit Normal");
        buttonLabel.FontWeight.Should().Be(FontWeight.SemiBold, "unaffected throughout");

        window.Close();
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
