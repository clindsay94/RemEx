using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using System.Text.RegularExpressions;
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

        var outcome = await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = "https://example.invalid/x?y=1" }, default);

        Assert.Equal("none", outcome);
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

        var outcome = await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = "" }, default);

        Assert.Equal("empty", outcome);
        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public async Task WhitespaceOnlyIsContentAndIsWritten()
    {
        // Someone who copied an indented code block copied the whitespace on purpose. The contrast
        // with the empty case above is the whole point: "looks like nothing" is not "is nothing".
        var clipboard = new FakeHostClipboard();

        Assert.Equal("none", await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = "    \n\t" }, default));
        Assert.Equal(1, clipboard.WriteCount);
    }

    [Fact]
    public async Task AnOversizePushIsRefUsedBeforeTheClipboardIsTouched()
    {
        var clipboard = new FakeHostClipboard();
        var tooBig = new string('a', ClipboardValidation.MaxPayloadBytes + 1);

        var outcome = await NewHandler(clipboard)
            .HandleClipboardPushAsync(new ClipboardPush { Text = tooBig }, default);

        Assert.Equal("too_large", outcome);
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

        Assert.Equal("too_large", await NewHandler(clipboard).HandleClipboardPushAsync(new ClipboardPush { Text = cjk }, default));
        Assert.Equal(0, clipboard.WriteCount);
    }

    [Fact]
    public async Task ExactlyTheCapIsAccepted()
    {
        // Off-by-one at the boundary, in the direction that would silently shrink the documented
        // limit. The rule is "larger than the cap is refused", not "at the cap".
        var clipboard = new FakeHostClipboard();
        var exact = new string('a', ClipboardValidation.MaxPayloadBytes);

        Assert.Equal("none", await NewHandler(clipboard).HandleClipboardPushAsync(new ClipboardPush { Text = exact }, default));
        Assert.Equal(ClipboardValidation.MaxPayloadBytes, clipboard.LastByteCount);
    }

    [Fact]
    public async Task APCWithNoWindowReportsFailureRatherThanThrowing()
    {
        // The host can be serving a phone before the desktop window exists. That is a state to
        // report, not an exception to unwind a socket loop with.
        var clipboard = new FakeHostClipboard { Succeeds = false };

        // "unavailable", NOT "none". Until RemEx-s1ay7 this returned a bool the caller discarded,
        // so a PC that could not take the clipboard was indistinguishable from one that did - and
        // the phone said "Sent to the PC's clipboard" either way.
        Assert.Equal("unavailable", await NewHandler(clipboard)
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

    [Fact]
    public void EveryOutcomeTokenTheHostEmitsIsOneThePhoneRecognises()
    {
        // THE CONTRACT THAT MAKES THE ANSWER USEFUL, and the reason it is scanned rather than
        // restated: the phone maps these tokens to sentences and fails CLOSED on anything else, so a
        // token misspelled on this side does not produce a wrong message - it produces "could not
        // send", silently turning a successful push into a reported failure. The literal that is
        // easiest to get wrong is "refused", which is written in the pairing gate far away from the
        // handler and is exercised by no unit test at all.
        //
        // (An earlier version of this test compared a list to itself and could not fail. It was
        // caught by reading it, not by running it - a green assertion proves nothing about whether
        // it can go red.)
        var source = File.ReadAllText(Path.Combine(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..")),
            "remex.agent", "Handlers", "PingPongHandler.cs"));

        // ANCHORED ON THE TYPE BEING CONSTRUCTED, not on the property name. Two earlier versions
        // of this scan were too wide and failed on correct code: one matched every `=> "..."` in the
        // file, the other matched `Reason = "closed"`, which is a DISCONNECT reason and nothing to
        // do with clipboards. A scan wide enough to catch everything is wide enough to catch the
        // wrong thing, and a guard that cries wolf gets an allowlist bolted on, which is how it
        // stops catching the real thing.
        //
        // The four tokens the handler RETURNS are pinned exactly by the behavioural tests above;
        // this covers the one written elsewhere and exercised by none of them.
        //
        // HONEST ABOUT WHAT IT PROVES: `known` is this side's copy of the phone's `when`, so it
        // checks the host against a list in the host's own test project, not against Kotlin. A
        // reviewer found it had ALREADY drifted at authoring time - the phone had no "unavailable"
        // arm and reached the right string only by falling through - so the arm was added rather
        // than the claim softened. Real cross-language enforcement would need the Kotlin source in
        // the scan; this is the cheap half, and its limit is stated so nobody reads it as the whole.
        var emitted = Regex.Matches(source, @"ClipboardPushResult\s*\{\s*Reason = ""([a-z_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(emitted);

        var known = new HashSet<string>(["none", "empty", "too_large", "unavailable", "refused"], StringComparer.Ordinal);
        Assert.True(
            emitted.IsSubsetOf(known),
            $"the host emits clipboard outcome tokens the phone does not map: {string.Join(", ", emitted.Except(known))}");

        // And the one no unit test reaches is definitely there.
        Assert.Contains("refused", emitted);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string p = "") => p;
}
