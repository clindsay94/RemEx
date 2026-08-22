using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Remex.Core.Services.Security;
using Remex.Desktop.Services;
using Remex.Desktop.Tests.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The pairing PIN panel: a copyable PIN, and an expired one replaced rather than dimmed
/// (RemEx-7ykyn items 1 and 2).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PairingPinCountdown"/> shipped with 9 tests and mutation verification under RemEx-scwy
/// and was consumed by nothing — the fourth component found in that state in this repo, after
/// PhonePresence, PairedDeviceDisplayName and the paired-device facts. The view model was deciding
/// expiry itself with a subtraction that had none of its properties.
/// </para>
/// <para>
/// These tests drive the VIEW MODEL, not the pure countdown, which already has its own. What was
/// missing was any proof that the panel consults it — the gap that let a fully tested component sit
/// unused.
/// </para>
/// </remarks>
public class PairingPinPanelTests
{
    private static readonly DateTimeOffset Issued = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A view model holding a PIN issued at a FIXED instant, with a clock the test drives.
    /// </summary>
    /// <remarks>
    /// THE FROZEN CLOCK IS THE WHOLE POINT, and it exists because the first version of this file did
    /// not have one. Reading DateTimeOffset.UtcNow meant the boundary cases PairingPinCountdown was
    /// written for were unreachable: I replaced the delegation with a plain subtraction and all eight
    /// tests still passed, because a few microseconds elapse between constructing the state and
    /// asserting on it, which is enough to make the two agree everywhere a real clock can reach.
    /// </remarks>
    private static ConnectionViewModel WithPin(TimeSpan validFor, TimeSpan elapsed)
    {
        var now = Issued;
        var vm = new ConnectionViewModel { PairingClock = () => now };
        vm.AttachEmbeddedPairingService(Mock.Of<IPairingService>());
        vm.ActivePairingPin = "123456";
        vm.ActivePairingExpiresAt = Issued + validFor;
        now = Issued + elapsed;
        return vm;
    }

    [Fact]
    public void AFreshPinIsShownAndIsNotWarning()
    {
        var vm = WithPin(TimeSpan.FromMinutes(3), elapsed: TimeSpan.Zero);

        vm.IsPairingPinExpired.Should().BeFalse();
        vm.IsPairingPinExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void ThePinWarnsInsideTheLastFifteenSeconds()
    {
        // The threshold comes from the TASK — read six digits off one screen and type them into a
        // phone, possibly one-handed. A warning with three seconds left tells the user something
        // they can no longer act on.
        var vm = WithPin(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(10));

        vm.IsPairingPinExpiringSoon.Should().BeTrue();
        vm.IsPairingPinExpired.Should().BeFalse("ten seconds left is still usable");
    }

    [Fact]
    public void AnExpiredPinIsReplacedRatherThanShown()
    {
        // REPLACE, NOT GREY OUT. An expired PIN rendered faintly is still six digits on a screen, and
        // a user looking at their phone rather than the PC will type them — the visual treatment
        // carries no information to the person actually doing the task.
        var vm = WithPin(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(4));

        vm.IsPairingPinExpired.Should().BeTrue();
    }

    [Fact]
    public void ExpiryIsInclusive()
    {
        // The host stops accepting the PIN AT the boundary, so an off-by-one here shows a countdown
        // reading zero beside a PIN the host has already refused — which reads as the host being
        // broken rather than as the PIN being old.
        // EXACTLY at the window, not a microsecond past it. This is the case a hand-rolled
        // `remaining < 0` gets wrong and a frozen clock is the only way to reach.
        var vm = WithPin(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(3));

        vm.IsPairingPinExpired.Should().BeTrue(
            "the host stops accepting the PIN AT the boundary, so a countdown reading zero beside a "
            + "PIN the host has already refused reads as the host being broken");
    }

    [Fact]
    public void ABackwardsClockCannotLendThePinMoreLifeThanItHas()
    {
        // An NTP correction makes elapsed negative, and a naive subtraction then reports MORE time
        // remaining than the window ever had — so the user acts on time they do not have. Reachable
        // only with a clock a test controls.
        var vm = WithPin(TimeSpan.FromSeconds(20), elapsed: TimeSpan.FromMinutes(-10));

        vm.PairingPinCountdownStatus.Remaining.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(20),
            "remaining can never exceed the window, whatever the clock does");
    }

    [Fact]
    public void WithNoPinAtAllNothingClaimsToBeExpired()
    {
        // The panel is not on screen in this state, and reporting "expired" would put a "get a new
        // PIN" call to action in front of a user who never asked for one.
        var vm = new ConnectionViewModel();

        vm.IsPairingPinExpired.Should().BeFalse();
        vm.IsPairingPinExpiringSoon.Should().BeFalse();
    }

    [Fact]
    public void TheCountdownIsDelegatedRatherThanRecomputed()
    {
        // A BEHAVIOURAL CHECK, not a source-text one: a hand-rolled subtraction would disagree with
        // the shipped component precisely at the edges it was written for. Zero-length window is the
        // sharpest — PairingPinCountdown calls it EXPIRED, on the grounds that it almost certainly
        // means a field that was never populated, while a naive "remaining > 0" call would too, but
        // a naive "remaining >= 0" would not.
        // A ZERO-LENGTH WINDOW. PairingPinCountdown calls it EXPIRED on the grounds that it almost
        // certainly means a field that was never populated; a subtraction of `expires - now` at the
        // same instant yields exactly zero, which `< 0` calls NOT expired. That is the one input
        // where the two provably disagree, and with a frozen clock it is reachable.
        var vm = WithPin(TimeSpan.Zero, elapsed: TimeSpan.Zero);

        vm.IsPairingPinExpired.Should().BeTrue(
            "a zero or negative validity window is expired, not unlimited");
    }

    [Fact]
    public void TheExpiryTickLeavesThePanelSTANDINGSoTheRetryIsReachable()
    {
        // THE TEST THAT WOULD HAVE CAUGHT THE WORST BUG IN THIS BEAD, and the sixth variant of the
        // same disease this session: nine tests drove view-model properties and NONE drove the timer,
        // where the state they assert on was being destroyed. The tick used to publish
        // IsPairingPinExpired and then, in the same callback, null the PIN and close the panel — so
        // the "expired, get a new one" branch was never painted, the card simply vanished, and
        // reopening it was gated on the PIN it had just cleared.
        var vm = WithPin(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(4));
        vm.ShowPairingPin = true;

        vm.OnPairingExpiryTick();

        vm.IsPairingPinExpired.Should().BeTrue("the tick must recognise the expiry");
        vm.ShowPairingPin.Should().BeTrue(
            "the panel has to stay up to say the PIN is dead and offer a new one — closing it at the "
            + "boundary is the vanishing-card behaviour this replaced");
        vm.HasActivePairingPin.Should().BeTrue(
            "IsPairingPinExpired is derived from the PIN still being set, so clearing it flips the "
            + "expired branch straight back off");
    }

    [Fact]
    public void ATickBeforeExpiryChangesNothing()
    {
        // The over-tightening control: a tick that closed the panel early, or declared a live PIN
        // dead, would be worse than the bug above.
        var vm = WithPin(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(1));
        vm.ShowPairingPin = true;

        vm.OnPairingExpiryTick();

        vm.IsPairingPinExpired.Should().BeFalse();
        vm.ShowPairingPin.Should().BeTrue();
    }

    [Fact]
    public void ThePanelOffersTheDigitsForCopyingAndSwapsThemWhenExpired()
    {
        // Avalonia binding failures are silent, so the view model proving the states exist says
        // nothing about whether the panel uses them. Scoped to the panel file, and asserting
        // PRESENCE rather than absence — the shape my guards have repeatedly got wrong this session.
        var panel = Regex.Replace(
            File.ReadAllText(Path.Combine(
                RepoRoot(), "remex.desktop", "Views", "PairingPinPanelView.axaml")),
            @"\s+", " ");

        panel.Should().Contain("<SelectableTextBlock",
            "the PIN must be copyable — a plain TextBlock made the one useful thing you can do with "
            + "a PIN on a PC impossible");
        panel.Should().Contain("Text=\"{Binding ActivePairingPin}\"",
            "the copyable element must be the one showing the PIN");

        // AND IT MUST NOT BE RENAMED. An AutomationProperties.Name on a content-bearing element
        // REPLACES its text for screen readers, so naming the digits "Copy PIN" would announce that
        // and never the PIN — on a value that is screen-only, expires in three minutes, and cannot be
        // re-read (review).
        var digits = Regex.Match(panel, "<SelectableTextBlock[^>]*>");
        digits.Success.Should().BeTrue();
        digits.Value.Should().NotContain("AutomationProperties.Name",
            "the digits ARE the accessible name; setting one hides them from screen readers");

        // THE DIGITS' OWN CONTAINER MUST CARRY THE HIDE, not merely some element in the file. My
        // first version asserted the file contained `IsVisible="{Binding !IsPairingPinExpired}"` —
        // and swapping the digits' Border to Opacity="0.4" still passed, because the hint TextBlock
        // below carries the same binding. An assertion satisfied by a different element than the one
        // it is about is the failure mode that has bitten this session repeatedly.
        // The lookahead tolerates a comment between the two, because there is one — and because a
        // guard that breaks when somebody explains the code next to it is a guard people delete.
        var digitsContainer = Regex.Match(
            panel, "<Border[^>]*>(?=\\s*(?:<!--.*?-->\\s*)?<SelectableTextBlock)", RegexOptions.Singleline);
        digitsContainer.Success.Should().BeTrue("the PIN digits must sit in their own container");
        digitsContainer.Value.Should().Contain("IsVisible=\"{Binding !IsPairingPinExpired}\"",
            "the digits themselves must HIDE when expired; dimming them is what this replaced, and a "
            + "faint six digits is still six digits to someone looking at their phone");
        panel.Should().Contain("IsVisible=\"{Binding IsPairingPinExpired}\"",
            "the expired state must have its own visible branch, or nothing replaces the digits");
        panel.Should().Contain("IsVisible=\"{Binding !IsPairingPinExpired}\"",
            "the digits must HIDE when expired; leaving them dimmed is what this replaced");
        panel.Should().Contain("Classes.expiring=\"{Binding IsPairingPinExpiringSoon}\"",
            "the last-15-seconds warning must reach the view");
    }

    [Fact]
    public void TheWarningColourIsAThemeTokenInEveryTheme()
    {
        // A literal here would survive the default theme and die on another. SolarFlare is the one
        // that catches contrast mistakes.
        //
        // RESOLVED, NOT READ RAW (RemEx-07jij). A preset file carries only its geometry now and
        // merges Themes/Shared/FallbackPalette.axaml for the colours, so File.ReadAllText on the
        // preset would report the brush missing from all four.
        var themes = ThemeDictionary.PresetNames;
        themes.Should().HaveCountGreaterThanOrEqualTo(4, "all four PC themes must be present to check");

        foreach (var theme in themes)
        {
            ThemeDictionary.ResolvedText(theme).Should().Contain("SystemWarningBrush",
                $"{theme} must define the brush the expiry warning uses");
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
