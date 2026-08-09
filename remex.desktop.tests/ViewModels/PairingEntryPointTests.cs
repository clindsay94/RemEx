using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Remex.Core.Services.Security;
using Remex.Desktop.Services.Security;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The "show pairing PIN" button is offered when this PC can actually produce one (RemEx-f66j).
/// </summary>
/// <remarks>
/// <para>
/// THE BUG THESE TESTS PIN IS THE WORST KIND: the affordance was not broken, it was ABSENT. Both views
/// gated the button on <c>!IsConnected</c> — the desktop's own WebSocket to its embedded host, which
/// RemEx-porg established is up essentially always — so the pairing entry point was hidden from most
/// users most of the time, and nothing failed, logged or looked wrong. A user who could not find how
/// to pair their phone had nothing to report except that the button was not there.
/// </para>
/// <para>
/// It was also the wrong property rather than an inverted one. <c>RevealPairingPinAsync</c> reaches
/// <c>IPairingService</c> in process or <c>IPairingPinQueryService</c> over IPC; neither touches that
/// socket. On the reading where the old gate looked deliberate — "offer pairing only while
/// disconnected" — it showed the button precisely when the services behind it were least likely to
/// be attached.
/// </para>
/// </remarks>
public class PairingEntryPointTests
{
    [Fact]
    public void WithNoPairingServiceAttachedTheButtonIsNotOffered()
    {
        // The honest hidden case, and the only one: a build with no way to produce a PIN should not
        // offer a button that cannot work.
        new ConnectionViewModel().CanRevealPairingPin.Should().BeFalse();
    }

    [Fact]
    public void AttachingTheEmbeddedPairingServiceOffersTheButton()
    {
        var vm = new ConnectionViewModel();

        vm.AttachEmbeddedPairingService(Mock.Of<IPairingService>());

        vm.CanRevealPairingPin.Should().BeTrue();
    }

    [Fact]
    public void AttachingTheStandaloneQueryServiceOffersTheButton()
    {
        // The IPC path, for a desktop talking to a host service in another process. It is a separate
        // attach point and was equally hidden by the old gate.
        var vm = new ConnectionViewModel();

        vm.AttachStandalonePairingPinQueryService(Mock.Of<IPairingPinQueryService>());

        vm.CanRevealPairingPin.Should().BeTrue();
    }

    [Fact]
    public void TheConnectionStateDoesNotDecideWhetherPairingIsOffered()
    {
        // THE REGRESSION ITSELF. A connected desktop with a pairing service attached must still offer
        // the button — that is the state nearly every user is in nearly all the time, and the state
        // the old gate hid it in.
        // BOTH ORDERS, AND THE FIRST BLOCK BELOW IS THE ONE THAT BITES. My original test attached the
        // service and THEN set IsConnected, so a regression computing the flag as `!IsConnected` AT
        // ATTACH TIME still passed — at that moment IsConnected was false. Injecting exactly that
        // showed the test green. A connection-derived flag only reveals itself when the connection
        // came first, which is also the real startup order: the desktop connects to its embedded host
        // before attaching to it. The second block is kept as the plain statement of the property.
        var connectedFirst = new ConnectionViewModel { IsConnected = true };
        connectedFirst.AttachEmbeddedPairingService(Mock.Of<IPairingService>());
        connectedFirst.CanRevealPairingPin.Should().BeTrue(
            "a desktop that is already connected when the pairing service attaches — the normal "
            + "startup order — must still offer the button");

        var vm = new ConnectionViewModel();
        vm.AttachEmbeddedPairingService(Mock.Of<IPairingService>());

        vm.IsConnected = true;

        vm.CanRevealPairingPin.Should().BeTrue(
            "the loopback link is up essentially always, so gating pairing on it hides the entry "
            + "point from most users most of the time (RemEx-porg, RemEx-f66j)");
    }

    [Fact]
    public void NoViewGatesThePairingButtonOnTheConnectionState()
    {
        // Avalonia binding changes are silent and a view-model property proves nothing about what the
        // axaml binds. Both views carried this bug independently, so both are checked.
        //
        // THE WHOLE FILE IS COLLAPSED TO ONE LINE FIRST, and an injection is what taught me to. The
        // first version scanned line by line for a line containing both the command and IsConnected —
        // but in SettingsView they sit on DIFFERENT LINES of the same Button, so putting the original
        // bug straight back left this test green. A guard that cannot see the bug it was written for
        // is worse than none. After normalising there are no line breaks left to hide a pair of
        // attributes from each other.
        // ONE COMMAND NOW, NOT TWO. RemEx-7ykyn item 3 collapsed the separate QR and PIN buttons into
        // a single pairing action, so GenerateQrCodeCommand IS the pairing entry point — it starts the
        // session, reveals the digits and draws the code. The property this test holds is unchanged:
        // that entry point must not be gated on the connection state.
        //
        // ENTRY POINTS ONLY. RemEx-7ykyn added a "get a new PIN" button to PairingPinPanelView that
        // also invokes the same command — but it is a RETRY inside a panel the user is already
        // looking at, not a way in, so gating it on CanRevealPairingPin would be meaningless (the
        // panel cannot be open unless a PIN was produced). The count floor below caught that addition
        // immediately, which is the floor doing its job: it made somebody decide which kind of button
        // this was instead of quietly counting it.
        // EXCLUDE THE ONE PANEL BY NAME — do not allowlist the views that happen to exist. My first
        // attempt filtered TO ConnectionView and SettingsView, which silenced this guard for every
        // view not yet written: a pairing button added to a tray flyout or an onboarding screen with
        // IsVisible bound to IsConnected would have been filtered out and the test stayed green
        // (review). That is me silencing my own guard while fixing it, which is worse than the count
        // failing.
        const string RetryPanel = "PairingPinPanelView.axaml";

        var buttons = Directory
            .GetFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(file => Path.GetFileName(file) != RetryPanel)
            .SelectMany(file =>
            {
                var flattened = Regex.Replace(File.ReadAllText(file), @"\s+", " ");
                return Regex
                    .Matches(flattened, "<Button[^>]*GenerateQrCodeCommand[^>]*>")
                    .Select(match => $"{Path.GetFileName(file)}: {match.Value}");
            })
            .ToArray();

        // A FLOOR, BECAUSE "FOUND NOTHING" AND "FOUND NOTHING WRONG" LOOK IDENTICAL OTHERWISE
        // (review). This is the THIRD way this one test managed to be inert: a rename of the command,
        // or the button becoming some other control, would leave the scan matching zero elements and
        // the emptiness assertion below trivially true. Two entry points exist; if that changes, this
        // number is the thing that makes somebody look.
        buttons.Should().HaveCount(2,
            "ConnectionView and SettingsView each offer the pairing button, and a scan that finds "
            + "neither is not a clean result — it is a guard that has stopped looking");

        // And the retry inside the panel still exists, so this test cannot be satisfied by deleting
        // the thing it just excused.
        var panel = Regex.Replace(
            File.ReadAllText(Path.Combine(
                RepoRoot(), "remex.desktop", "Views", "PairingPinPanelView.axaml")),
            @"\s+", " ");
        panel.Should().Contain("GenerateQrCodeCommand",
            "an expired PIN is REPLACED by a get-a-new-one action, so the panel must offer one");

        // POSITIVE, not a banned-substring check. Asserting the absence of IsConnected would still
        // pass if IsVisible were deleted outright, or moved to an enclosing StackPanel gated on the
        // connection — which is the likelier regression shape, since hiding the whole row is the
        // lazy fix.
        buttons.Should().OnlyContain(
            button => button.Contains("IsVisible=\"{Binding CanRevealPairingPin}\"", StringComparison.Ordinal)
                   || button.Contains("IsVisible=\"{Binding Connection.CanRevealPairingPin}\"", StringComparison.Ordinal),
            "the pairing button must be gated on whether a pairing path is wired up, not on the "
            + "loopback link — which is up essentially always, so gating on it hides pairing from "
            + "most users most of the time (RemEx-porg, RemEx-f66j)");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
