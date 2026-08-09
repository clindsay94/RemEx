using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models.IPC;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The QR code and the PIN are one act, not two buttons (RemEx-7ykyn, item 3).
/// </summary>
/// <remarks>
/// <para>
/// They were always ONE pairing session — <c>GenerateQrCodeAsync</c> starts it and encodes that very
/// PIN into the payload — but the two were surfaced as separate buttons opening separate panels, so a
/// user had to choose before knowing which their phone could use. A phone that cannot scan (bad
/// light, no camera permission, a tablet across the room) needs the digits, and the digits it needs
/// are the ones already inside the code on screen.
/// </para>
/// <para>
/// MOSTLY VIEW ASSERTIONS, because that is where this bead lives. Avalonia binding failures are
/// silent and a view-model test says nothing about which command a button is wired to — which is
/// exactly the mistake being corrected here, since the old wiring was two commands that each did half
/// the job.
/// </para>
/// </remarks>
public class PairingQrAndPinTogetherTests
{
    [Fact]
    public async Task ShowingThePairingCodeShowsTheDigitsToo()
    {
        // THE JOIN. GenerateQrCodeAsync already set ActivePairingPin — the payload carries it — and
        // still left the PIN panel hidden. Nothing about that was visible from the view model's own
        // state, which is why it survived this long.
        //
        // active: false, AND THE BEFORE-ASSERTION, BECAUSE THE FIRST VERSION OF THIS WAS INERT. With
        // the default fake, AttachEmbeddedPairingService finds an active PIN and opens the panel
        // itself — so the arrangement satisfied the assertion and deleting the production line
        // changed nothing. Found by injection, not by reading it. A service with no pin in flight is
        // also the honest case: the user pressing this button is starting a pairing, not resuming one.
        var vm = new ConnectionViewModel();
        vm.AttachEmbeddedPairingService(new FakePairingService(active: false));
        vm.ShowPairingPin.Should().BeFalse("the panel must start shut, or the act below proves nothing");

        await vm.GenerateQrCodeCommand.ExecuteAsync(null);

        // ShowQrCode is deliberately NOT asserted: building the image needs an initialised Avalonia
        // platform, which a plain unit test has none of, so it would be measuring the harness. That
        // is also why the reveal happens where the PIN is obtained rather than after the bitmap — a
        // rendering failure must not cost the user the half that still works.
        vm.ShowPairingPin.Should().BeTrue(
            "a phone that cannot scan needs the digits, and they belong to this same session");
        vm.ActivePairingPin.Should().Be("123456", "the digits shown are the ones inside the code");
    }

    [Fact]
    public async Task TheINSTALLEDAgentPathRevealsTheDigitsToo()
    {
        // THE SIXTEENTH INERT GUARD, found in review: the embedded test above kills BOTH call sites
        // when the method body is emptied, so it looked like coverage for a reveal that has two
        // separate call sites. Delete only the standalone one and the suite stayed green — and the
        // standalone path is the NORMAL installed shape, where remex.agent runs as its own process.
        var vm = new ConnectionViewModel();
        vm.AttachStandalonePairingPinQueryService(new FakeStandalonePairingPinQueryService(
            new PairingPinInfo("654321", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())));
        vm.ShowPairingPin.Should().BeFalse("the panel must start shut, or the act below proves nothing");

        await vm.GenerateQrCodeCommand.ExecuteAsync(null);

        vm.ShowPairingPin.Should().BeTrue("a phone paired against the installed agent needs the digits too");
        vm.ActivePairingPin.Should().Be("654321");
    }

    [Fact]
    public async Task WithNoPairingServiceTheDigitsPanelStaysShut()
    {
        // FAILS CLOSED on the panel, not on the button: with nothing attached there is no PIN, and an
        // empty six-digit panel beside a QR is worse than no panel. The QR's own ungated-command
        // problem was fixed separately (RemEx-f66j) — both hosts gate the button on
        // CanRevealPairingPin, which the view scan below re-checks.
        var vm = new ConnectionViewModel();

        await vm.GenerateQrCodeCommand.ExecuteAsync(null);

        vm.ShowPairingPin.Should().BeFalse("there is no PIN to show");
    }

    [Theory]
    [InlineData("ConnectionView.axaml", "GenerateQrCodeCommand", "CanRevealPairingPin")]
    [InlineData("SettingsView.axaml", "Connection.GenerateQrCodeCommand", "Connection.CanRevealPairingPin")]
    public void EachHostOffersONEPairingButton(string view, string command, string gate)
    {
        // ONE, not two, and asserted with a FLOOR as well as a ceiling: "no button binds
        // RevealPairingPinCommand" would pass just as happily if somebody deleted the pairing button
        // altogether, which is the failure this whole area started with (RemEx-f66j).
        var flattened = Flatten(view);

        var buttons = Regex.Matches(flattened, "<Button[^>]*>")
            .Select(m => m.Value)
            .Where(b => b.Contains(command))
            .ToArray();

        buttons.Should().ContainSingle("the QR and the PIN are one act behind one button");
        buttons[0].Should().Contain(gate, "an ungated button offers a QR that pairs with nothing");
        flattened.Should().NotContain("RevealPairingPinCommand",
            "a second button for the other half is what this replaced");
    }

    [Fact]
    public void GettingANewPinReplacesTheQrBesideIt()
    {
        // Now that both panels open together, a refresh that renewed only the PIN would leave the QR
        // encoding the session that just expired — a code that scans and pairs with nothing, which is
        // strictly worse than the expired digits it sits next to.
        var panel = Flatten("PairingPinPanelView.axaml");

        var refresh = Regex.Matches(panel, "<Button[^>]*>")
            .Select(m => m.Value)
            .Where(b => b.Contains("Settings_PairingPinGetNew"))
            .ToArray();

        refresh.Should().ContainSingle("the expired panel's whole point is one way back");
        refresh[0].Should().Contain("GenerateQrCodeCommand",
            "renewing the PIN must renew the code that carries it");
    }

    [Fact]
    public void TheQrPlateIsBigEnoughToScanAcrossADesk()
    {
        var panel = Flatten("PairingQrPanelView.axaml");

        var image = Regex.Match(panel, "<Image[^>]*QrCodeImage[^>]*>");
        image.Success.Should().BeTrue("the plate must exist to be measured");

        var width = int.Parse(Regex.Match(image.Value, @"Width=""(\d+)""").Groups[1].Value);
        var height = int.Parse(Regex.Match(image.Value, @"Height=""(\d+)""").Groups[1].Value);

        width.Should().BeGreaterThan(180, "180 device-independent pixels is a small target on a 4K panel");
        height.Should().Be(width, "a QR stretched by one axis stops decoding");
    }

    // ── Harness ────────────────────────────────────────────────────────────────

    private static string Flatten(string viewFileName) => Regex.Replace(
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", viewFileName)),
        @"\s+", " ");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
