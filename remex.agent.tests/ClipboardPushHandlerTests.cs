using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Core.Validation;

namespace Remex.Agent.Tests;

/// <summary>
/// The host end of <c>clipboard_push</c>: what reaches the PC clipboard and what does not (RemEx-hgqs).
/// </summary>
/// <remarks>
/// <para>
/// **THE HOST RE-VALIDATES WHAT THE PHONE ALREADY CHECKED, AND THESE TESTS ARE WHY THAT IS NOT
/// REDUNDANT.** The Android sender runs the same rule through the native export before sending, so
/// on a healthy pair nothing here ever refuses anything. The cap exists to bound what a PEER can
/// make this machine hold, and a bound enforced only by the peer is not a bound — an older, modified
/// or buggy client is precisely the case, and it is the only case these tests describe.
/// </para>
/// <para>
/// The collaborators are <c>null!</c> for the same reason the held-key tests use that technique: a
/// primary constructor captures only what is used, and this path touches the clipboard and nothing
/// else. Building a telemetry service and a pairing handler for a test about a string would obscure
/// what is being checked.
/// </para>
/// <para>
/// WHAT THESE DO NOT PROVE, stated rather than left for someone to assume: that the <c>switch</c> in
/// <c>HandleAsync</c> still routes <c>clipboard_push</c> to this method. Deleting that one case
/// leaves every test in this file green. That gap is the RemEx-y6x6 shape and it closes with a real
/// round trip, not with another unit test.
/// </para>
/// </remarks>
public class ClipboardPushHandlerTests
{
    private static PingPongHandler NewHandler(FakeHostClipboard clipboard) =>
        new(
            NullLogger<PingPongHandler>.Instance,
            null!,
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(),
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IDashboardProfileStorageService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<Remex.Agent.Services.IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new Remex.Agent.Services.ClientSessionRegistry(),
            null!,
            null!,
            clipboard);

    [Fact]
    public async Task AcceptableTextReachesTheClipboardIntact()
    {
        var clipboard = new FakeHostClipboard();

        var written = await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = "https://example.invalid/x?y=1" }, default);

        Assert.True(written);
        Assert.Equal(1, clipboard.WriteCount);
        Assert.Equal("https://example.invalid/x?y=1", clipboard.LastText);
    }

    [Fact]
    public async Task AnEmptyPushNEVERReachesTheClipboard()
    {
        // THE ONE THAT DESTROYS SOMETHING. Writing an empty string would CLEAR the PC's clipboard,
        // silently throwing away whatever the user deliberately put there - so the assertion that
        // matters is not the return value but that the write never happened at all.
        var clipboard = new FakeHostClipboard();

        var written = await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = "" }, default);

        Assert.False(written);
        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public async Task WhitespaceOnlyIsContentAndIsWritten()
    {
        // Someone who copied an indented code block copied the whitespace on purpose. The contrast
        // with the empty case above is the whole point: "looks like nothing" is not "is nothing".
        var clipboard = new FakeHostClipboard();

        Assert.True(await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = "    \n\t" }, default));
        Assert.Equal(1, clipboard.WriteCount);
    }

    [Fact]
    public async Task AnOversizePushIsRefUsedBeforeTheClipboardIsTouched()
    {
        var clipboard = new FakeHostClipboard();
        var tooBig = new string('a', ClipboardValidation.MaxPayloadBytes + 1);

        var written = await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = tooBig }, default);

        Assert.False(written);
        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public async Task TheCapIsCountedInUtf8BytesNotCharacters()
    {
        // THE ONE A CHARACTER-COUNTING IMPLEMENTATION FAILS, and the reason the rule is shared rather
        // than reimplemented on each side. These characters are three UTF-8 bytes each, so a payload
        // that is comfortably under the cap by character count is over it by the measure the wire
        // actually uses - and this would be a limit that only leaked for people writing in Chinese,
        // Japanese or Korean.
        var clipboard = new FakeHostClipboard();
        var cjk = new string('世', (ClipboardValidation.MaxPayloadBytes / 3) + 1);

        Assert.True(cjk.Length < ClipboardValidation.MaxPayloadBytes, "the point is it passes a char-count check");

        Assert.False(await NewHandler(clipboard).HandleClipboardPushAsync(new ClipboardPush { Text = cjk }, default));
        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public async Task ExactlyTheCapIsAccepted()
    {
        // Off-by-one at the boundary, in the direction that would silently shrink the documented
        // limit. The rule is "larger than the cap is refused", not "at the cap".
        var clipboard = new FakeHostClipboard();
        var exact = new string('a', ClipboardValidation.MaxPayloadBytes);

        Assert.True(await NewHandler(clipboard).HandleClipboardPushAsync(new ClipboardPush { Text = exact }, default));
        Assert.Equal(ClipboardValidation.MaxPayloadBytes, clipboard.LastByteCount);
    }

    [Fact]
    public async Task APCWithNoWindowReportsFailureRatherThanThrowing()
    {
        // The host can be serving a phone before the desktop window exists. That is a state to
        // report, not an exception to unwind a socket loop with.
        var clipboard = new FakeHostClipboard { Succeeds = false };

        Assert.False(await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = "x" }, default));
        Assert.Equal(1, clipboard.WriteCount);
    }

    [Fact]
    public void ClipboardPushRequiresPairing()
    {
        // THE GATE IS A DEFAULT, WHICH IS WHY IT NEEDS ASSERTING. Nothing in the switch checks
        // pairing for this type; it is covered because RequiresPairing ends `_ => true`. That is
        // good design and completely invisible - a future "let the clipboard work before pairing"
        // convenience adding `MessageTypes.ClipboardPush => false` would compile and leave every
        // other test in this repo green, while any peer on the LAN could write the PC's clipboard.
        Assert.True(PingPongHandler.RequiresPairing(MessageTypes.ClipboardPush));

        // The neighbours, so this reads as the rule it is rather than a lone fact: the handshake
        // types are exempt because they are HOW a connection authenticates.
        Assert.False(PingPongHandler.RequiresPairing(MessageTypes.PairingRequest));
        Assert.False(PingPongHandler.RequiresPairing(MessageTypes.Ping));
    }

    [Fact]
    public void ClipboardPushIsNotRestrictedToThePCItself()
    {
        // The counterpart, and the reason the two gates are separate. RequiresLoopback is "is this
        // connection the PC itself", and it exists for the launcher allowlist because LAUNCHAPP is
        // measured against it. A clipboard push from a paired phone is the whole feature, so it must
        // NOT be on that list - asserting it keeps a future tightening from silently disabling this.
        Assert.False(PingPongHandler.RequiresLoopback(MessageTypes.ClipboardPush));
    }
}
