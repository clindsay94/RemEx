using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Moq;
using Remex.Core.Services.Security;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The pairing QR dies with the PIN it encodes (RemEx-hprgb).
/// </summary>
/// <remarks>
/// <para>
/// The two panels are independent and can both be open. The QR embeds the live PIN, and the expiry
/// path used to touch only the PIN — so the PC said "this PIN has expired, get a new one" while,
/// twelve pixels away, offering a code encoding that exact PIN. A phone that scanned it got a
/// pairing failure with nothing on either screen explaining why.
/// </para>
/// <para>
/// THE PIN IS KEPT AND THE QR IS NOT, deliberately. The panel can say a PIN is dead and offer a
/// replacement; a QR can only be scanned, so there is nothing for it to say and no safe way to leave
/// it up.
/// </para>
/// </remarks>
public class PairingQrExpiryTests
{
    private static readonly DateTimeOffset Issued = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A view model holding a PIN and a QR, with a clock the test drives.
    /// </summary>
    /// <remarks>
    /// The frozen clock is not optional here: the expiry boundary is inclusive and unreachable from a
    /// test that reads <c>UtcNow</c>, which is why <c>PairingClock</c> exists (RemEx-7ykyn).
    /// </remarks>
    private static ConnectionViewModel WithPinAndQr(TimeSpan validFor, TimeSpan elapsed)
    {
        var now = Issued;
        var vm = new ConnectionViewModel { PairingClock = () => now };
        vm.AttachEmbeddedPairingService(Mock.Of<IPairingService>());
        vm.ActivePairingPin = "123456";
        vm.ActivePairingExpiresAt = Issued + validFor;
        vm.ShowPairingPin = true;
        vm.ShowQrCode = true;
        now = Issued + elapsed;
        return vm;
    }

    [Fact]
    public void WhenThePinExpiresTheQrGoesWithIt()
    {
        var vm = WithPinAndQr(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(4));

        vm.OnPairingExpiryTick();

        vm.ShowQrCode.Should().BeFalse(
            "the QR encodes the PIN, and a code the host has stopped accepting can only produce a "
            + "pairing failure the user cannot diagnose");

        // NO ASSERTION ON QrCodeImage, deliberately, and this is a stated gap rather than an
        // oversight. The first version asserted it was null — which it already was, because this
        // harness never sets one, so the assertion passed with the fix, without the fix, and with
        // CloseQrCode's body deleted (review). It read as coverage of the one thing I said I was
        // least sure about. Making it real needs an Avalonia Bitmap, and this test project has no
        // headless Avalonia package, so constructing one throws for want of a render platform.
        // Image disposal is therefore unreachable from here and is covered by CloseQrCode being the
        // single teardown, exercised by the Close button too.
    }

    [Fact]
    public void ThePinPanelStaysUpEvenThoughTheQrDoesNot()
    {
        // THE ASYMMETRY IS THE DESIGN, so it gets its own assertion rather than being implied. A
        // panel can explain itself; a QR cannot.
        var vm = WithPinAndQr(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(4));

        vm.OnPairingExpiryTick();

        vm.ShowPairingPin.Should().BeTrue();
        vm.IsPairingPinExpired.Should().BeTrue();
        vm.ShowQrCode.Should().BeFalse();
    }

    [Fact]
    public void AConsumedPinTakesTheQrWithIt()
    {
        // THE PATH THE FIRST FIX MISSED, and the one that actually happens. The PIN is cleared when
        // the pairing session is CONSUMED — the success path of every pairing — and that handler
        // stops the expiry timer, so the retire wired into the tick could never run. A phone would
        // pair, and the QR encoding the burned PIN would sit there indefinitely ready to hand the
        // next scan a dead code (review).
        var vm = WithPinAndQr(TimeSpan.FromMinutes(3), elapsed: TimeSpan.Zero);

        vm.ClearActivePairingPinForTests();

        vm.ShowQrCode.Should().BeFalse("a consumed PIN leaves nothing for the QR to encode");
        vm.HasActivePairingPin.Should().BeFalse();
        vm.ShowPairingPin.Should().BeFalse(
            "unlike expiry, there is no dead PIN to explain here — the pairing succeeded");
    }

    [Fact]
    public void ALiveQrIsLeftAlone()
    {
        // The over-tightening control, and it matters: a tick that retired the QR early would close
        // it under a user who was mid-scan, which is worse than the bug being fixed.
        var vm = WithPinAndQr(TimeSpan.FromMinutes(3), elapsed: TimeSpan.FromMinutes(1));

        vm.OnPairingExpiryTick();

        vm.ShowQrCode.Should().BeTrue();
        vm.ShowPairingPin.Should().BeTrue();
    }

    [Fact]
    public void TheQrButtonIsGatedOnBeingAbleToProduceAPin()
    {
        // Both reach the same two services, so a build that cannot produce a PIN cannot produce a
        // useful QR either — GenerateQrCodeAsync would serialize a payload with pin=null, which is a
        // code that scans and then cannot pair.
        //
        // ASSERTED PER BUTTON, not as a file-wide substring: the count floor is what stops this going
        // quiet if a button is renamed or a third view appears, which is the failure mode that has
        // bitten every guard I wrote this session.
        // EXCLUDE THE RETRY PANEL BY NAME, not by allowlisting the views that happen to exist: since
        // RemEx-7ykyn item 3 the panel's own "get a new one" button invokes this same command, and it
        // is a retry inside a panel that cannot be open unless a PIN was produced, so gating it would
        // be meaningless. Excluding by name keeps a pairing button added to some future view inside
        // the scan.
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

        buttons.Should().HaveCount(2,
            "ConnectionView and SettingsView each offer the QR button; a scan finding neither is a "
            + "guard that has stopped looking, not a clean result");

        buttons.Should().OnlyContain(
            button => button.Contains("IsVisible=\"{Binding CanRevealPairingPin}\"", StringComparison.Ordinal)
                   || button.Contains("IsVisible=\"{Binding Connection.CanRevealPairingPin}\"", StringComparison.Ordinal),
            "a QR button offered where no PIN can be produced yields a code carrying pin=null");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
