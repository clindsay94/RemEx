using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Guards <c>ShellViewModel.ShowPresencePulse</c> (RemEx-d7xj8) — the flag that gates the
/// drawer-footer presence badge's pulse animation. It has to be true only while a phone is
/// attached AND the user has not asked for reduced motion, and it has to stay live as either
/// input changes after construction.
/// </summary>
/// <remarks>
/// A SOURCE SCAN, deliberately, matching <see cref="PaletteTransitionSuppressionTests"/>'s
/// treatment of the sibling <c>SuppressPaletteTransitions</c> flag. <c>ShellViewModel</c> takes a
/// full DI graph (<c>DashboardLayoutService</c>, <c>ThemeService</c>, <c>HardwareThemeService</c>,
/// <c>ConnectionViewModel</c>, <c>IServiceProvider</c>) that nothing in this test project
/// constructs directly — there is no <c>new ShellViewModel(...)</c> anywhere in this suite to
/// follow. Reading the wiring is what every other test touching this class already does instead.
/// </remarks>
public class ShellPresencePulseTests
{
    [Fact]
    public void ShowPresencePulseRequiresBothAnAttachedPhoneAndMotionNotReduced()
    {
        ShellViewModelSource().Should().MatchRegex(
            @"public bool ShowPresencePulse => Presence\.IsPhoneAttached && !IsReducedMotion;",
            "the pulse has to require an attached phone AND motion not being reduced — either " +
            "input alone is not enough");
    }

    [Fact]
    public void ReducedMotionChangingReRaisesThePulseFlag()
    {
        // A computed property notifies nothing on its own. Without this, toggling reduced motion
        // mid-session leaves the .pulse class binding stale and the badge keeps animating (or
        // stays still) regardless of what the user just asked for.
        ExtractMethod(ShellViewModelSource(), "OnIsReducedMotionChanged").Should()
            .Contain("OnPropertyChanged(nameof(ShowPresencePulse));",
                "OnIsReducedMotionChanged has to re-raise ShowPresencePulse or the badge's pulse " +
                "class goes stale the moment reduced motion is toggled");
    }

    [Fact]
    public void PresenceAttachmentChangingReRaisesThePulseFlagToo()
    {
        var shell = ShellViewModelSource();

        // PhonePresenceMonitor.Instance is a process-wide singleton ShellViewModel does not own, so
        // the only way ShowPresencePulse tracks it live is an explicit subscription — there is no
        // ObservableProperty machinery to do it automatically the way OnIsReducedMotionChanged does.
        shell.Should().Contain("Presence.PropertyChanged += _onPresenceChanged;",
            "without subscribing to the presence monitor, the badge's pulse would only update on " +
            "the next unrelated notification, not when the phone actually attaches or detaches");

        shell.Should().MatchRegex(
            @"nameof\(PhonePresenceMonitor\.IsPhoneAttached\)[\s\S]{0,80}OnPropertyChanged\(nameof\(ShowPresencePulse\)\)",
            "the presence subscription has to re-raise ShowPresencePulse specifically when " +
            "IsPhoneAttached changes");
    }

    [Fact]
    public void DisposeUnsubscribesFromThePresenceMonitor()
    {
        // ShellViewModel already unsubscribes from every other external event it hooks in its own
        // Dispose (ThemeService.CustomizationApplied, Connection.PropertyChanged) — the presence
        // subscription added for the pulse has to follow the same pattern rather than leaking.
        ExtractMethod(ShellViewModelSource(), "Dispose").Should()
            .Contain("Presence.PropertyChanged -= _onPresenceChanged;",
                "Dispose has to unsubscribe the presence handler alongside the other event " +
                "subscriptions it already tears down");
    }

    /// <summary>
    /// Everything from a method's opening brace to the matching close at class indent (four
    /// spaces). Same heuristic <c>PaletteTransitionSuppressionTests</c> and
    /// <c>CommandPaletteLightDismissTests</c> use, for the same reason.
    /// </summary>
    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape — update this test");
        return match.Value;
    }

    private static string ShellViewModelSource([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
