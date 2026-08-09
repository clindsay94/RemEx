using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models.IPC;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// A pairing failure reaches the user even when the window is not on screen (RemEx-7ykyn, item 4).
/// </summary>
/// <remarks>
/// <para>
/// <c>StatusText</c> alone was not feedback. RemEx closes to the tray by default, so for most of the
/// app's running life the pairing surface announced its failures to a window nobody was looking at: a
/// user who pressed "Pair a phone", switched to their phone and found nothing happening had no way to
/// learn why. <see cref="NotificationRouter"/> already knew what to do with a
/// <see cref="NotificationImportance.Problem"/> — nothing was asking it.
/// </para>
/// <para>
/// AND THE PIN IS NOT IN IT. Only failures are announced. Six digits in a balloon would put the
/// pairing secret on a lock screen and in the OS notification history; the PIN is screen-only, in
/// front of whoever is sitting at the PC, which is the entire basis on which showing it is safe.
/// </para>
/// </remarks>
public class PairingFailureReachesTheUserTests : IDisposable
{
    private readonly List<(NotificationImportance Importance, string Title, string Message)> _announced = [];
    private readonly IInAppNotificationSink? _savedInApp = NotificationService.Instance.InApp;
    private readonly Func<bool>? _savedProbe = NotificationService.Instance.WindowVisibleProbe;
    private readonly Action<Action> _savedDispatch = NotificationService.Instance.Dispatch;

    public PairingFailureReachesTheUserTests()
    {
        // Dispatch inline: the real one posts to Dispatcher.UIThread, which a plain unit test has
        // none of, so the announcement would simply never arrive and every assertion below would be
        // measuring the harness rather than the code.
        NotificationService.Instance.Dispatch = static work => work();
        NotificationService.Instance.WindowVisibleProbe = () => true;
        NotificationService.Instance.InApp = new RecordingSink(_announced);
    }

    public void Dispose()
    {
        NotificationService.Instance.InApp = _savedInApp;
        NotificationService.Instance.WindowVisibleProbe = _savedProbe;
        NotificationService.Instance.Dispatch = _savedDispatch;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task AHostThatCannotProduceAPinIsANNOUNCED_NotJustWrittenToTheStatusLine()
    {
        // The standalone query service answering "no pin" is the reachable failure: the installed
        // agent is running but pairing could not start. Before this it set a status line and drew
        // nothing else — and the status line is inside the window that is usually closed.
        var vm = new ConnectionViewModel();
        vm.AttachStandalonePairingPinQueryService(new FakeStandalonePairingPinQueryService((PairingPinInfo?)null));

        await vm.GenerateQrCodeCommand.ExecuteAsync(null);

        _announced.Should().ContainSingle("a pairing failure the user cannot see is the bug being fixed");
        _announced[0].Importance.Should().Be(NotificationImportance.Problem,
            "Problem is the importance the router will carry to a tray balloon once the window is hidden");

        // THE EXACT STRING, not NotBeEmpty (review). StatusText is initialised to "Disconnected" and
        // never set to empty, so the emptiness form passed on a view model that had never run the
        // command — leaving the half of AnnouncePairingProblem the doc argues hardest for, that the
        // record survives the upgrade to notifications, with no guard at all.
        vm.StatusText.Should().Be(
            Remex.Desktop.Services.LocalizationService.Instance["Status_FailedGeneratePinHost"],
            "the status line is the RECORD, and it stays");
    }

    [Fact]
    public async Task WithNOPairingServiceAtAllTheUserIsStillTold()
    {
        // THE BRANCH THE REVIEW FOUND DEAD, and the one neither other test reaches because both
        // attach something. The fallback used to be gated on string.IsNullOrEmpty(StatusText) — and
        // StatusText is initialised to "Disconnected" and never set to empty by any of its
        // assignments — so pressing "Pair a phone" with nothing attached produced no PIN, no code and
        // no message at all. Silence is the defect this whole bead exists to remove.
        var vm = new ConnectionViewModel();

        await vm.GenerateQrCodeCommand.ExecuteAsync(null);

        _announced.Should().ContainSingle("a button that does nothing and says nothing is the worst outcome here");
        _announced[0].Message.Should().Be(
            Remex.Desktop.Services.LocalizationService.Instance["Status_PairingServiceUnavailable"]);
        vm.ShowQrCode.Should().BeFalse("no PIN means no code worth drawing");
    }

    [Fact]
    public async Task ASUCCESSFULPairingAnnouncesNothingAndLeaksNoDigits()
    {
        // THE NEGATIVE HALF, and the one that matters most. A toast or balloon carrying the PIN
        // would put the pairing secret on a lock screen and in the OS notification history. Success
        // is already visible — the panel opens with the digits in it, in front of whoever is sitting
        // at the PC, which is the whole basis on which showing them is safe.
        var vm = new ConnectionViewModel();
        vm.AttachStandalonePairingPinQueryService(new FakeStandalonePairingPinQueryService(
            new PairingPinInfo("654321", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())));

        await vm.GenerateQrCodeCommand.ExecuteAsync(null);

        vm.ActivePairingPin.Should().Be("654321", "the arrangement must actually produce a PIN, or this proves nothing");
        _announced.Should().BeEmpty(
            "success is already visible in the panel; there is nothing here worth interrupting for");

        // VACUOUS TODAY AND DELIBERATELY KEPT, which is worth saying out loud rather than leaving
        // for someone to discover: it passes because nothing is announced at all. Its job starts the
        // day somebody adds a success announcement — "your phone can pair now" is a reasonable thing
        // to want, and building it by passing the PIN along is the obvious way to do it wrong.
        _announced.Should().NotContain(
            a => a.Message.Contains("654321") || a.Title.Contains("654321"),
            "the PIN must never leave the screen it is shown on");
    }

    private sealed class RecordingSink(List<(NotificationImportance, string, string)> announced)
        : IInAppNotificationSink
    {
        public void Show(NotificationImportance importance, string title, string message)
            => announced.Add((importance, title, message));
    }
}
