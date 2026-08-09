using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Core.Messages;
using Remex.Core.Services;
using Remex.Core.Validation;

namespace Remex.Agent.Tests;

/// <summary>
/// The host end of <c>clipboard_request</c>: what the PC sends back, and what it withholds
/// (RemEx-ci98m).
/// </summary>
/// <remarks>
/// <para>
/// **THIS DIRECTION HAS TO CARRY ITS REFUSALS, WHICH THE PUSH DIRECTION DID NOT.** A push can decide
/// every refusal on the phone before anything crosses the wire; a fetch cannot, because only the PC
/// knows what is on its own clipboard. So "empty" and "too large" become answers rather than local
/// checks — and the thing worth testing is that a refusal carries NO TEXT, not an empty string.
/// </para>
/// <para>
/// The distinction these tests exist for is null versus empty from the clipboard itself. Null means
/// it could not be read — no window yet, a platform refusal — and empty means it was read and holds
/// nothing. Collapsing them would report a PC whose UI has not started as a PC with nothing copied,
/// which sends the user off to copy something that would not have helped.
/// </para>
/// </remarks>
public class ClipboardFetchHandlerTests
{
    private static PingPongHandler NewHandler(FakeHostClipboard clipboard) =>
        new(
            NullLogger<PingPongHandler>.Instance, null!,
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(), Mock.Of<IAppLauncherService>(),
            Mock.Of<IDashboardProfileStorageService>(), Mock.Of<IProcessMonitorService>(),
            Mock.Of<Remex.Agent.Services.IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            null!, null!, null!, null!, null!, null!,
            new Remex.Agent.Services.ClientSessionRegistry(), null!, null!, clipboard);

    [Fact]
    public async Task ClipboardTextIsSentWithReasonNone()
    {
        var clipboard = new FakeHostClipboard { Contents = "https://example.invalid/x" };

        var answer = await NewHandler(clipboard).ReadClipboardForClientAsync(default);

        Assert.Equal("none", answer.Reason);
        Assert.Equal("https://example.invalid/x", answer.Text);
        Assert.Equal(1, clipboard.ReadCount);
    }

    [Fact]
    public async Task AnUnreadableClipboardIsUnavailableAndNotEmpty()
    {
        // The two are different stories and the phone says different things about them. Reporting a
        // PC whose window has not started as "nothing is copied" sends someone off to copy something
        // again, which was never the problem.
        var answer = await NewHandler(new FakeHostClipboard { Contents = null }).ReadClipboardForClientAsync(default);

        Assert.Equal("unavailable", answer.Reason);
        Assert.Null(answer.Text);
    }

    [Fact]
    public async Task AnEmptyClipboardSendsNoTextAtAll()
    {
        // NOT AN EMPTY STRING. A phone that put "" on its own clipboard would destroy whatever the
        // user had copied there - the same harm the push direction refuses an empty payload to
        // avoid, arriving from the other side.
        var answer = await NewHandler(new FakeHostClipboard { Contents = "" }).ReadClipboardForClientAsync(default);

        Assert.Equal("empty", answer.Reason);
        Assert.Null(answer.Text);
    }

    [Fact]
    public async Task AnOversizeClipboardIsRefusedAndCarriesNoText()
    {
        // The cap applies in this direction too, and not for symmetry's sake: a PC clipboard can
        // hold a whole document someone copied out of an editor, and shipping that to a phone on a
        // slow link is the same stall the push refuses, pointed the other way.
        var clipboard = new FakeHostClipboard { Contents = new string('a', ClipboardValidation.MaxPayloadBytes + 1) };

        var answer = await NewHandler(clipboard).ReadClipboardForClientAsync(default);

        Assert.Equal("too_large", answer.Reason);
        Assert.Null(answer.Text);
    }

    [Fact]
    public async Task WhitespaceOnlyIsContentAndIsSent()
    {
        var answer = await NewHandler(new FakeHostClipboard { Contents = "  \n\t" }).ReadClipboardForClientAsync(default);

        Assert.Equal("none", answer.Reason);
        Assert.Equal("  \n\t", answer.Text);
    }

    [Fact]
    public void BothClipboardTypesRequirePairing()
    {
        // Same default-secure gate as the push, asserted for the same reason: nothing in the switch
        // checks pairing: it is covered because RequiresPairing ends `_ => true`. Reading the PC's
        // clipboard is if anything the more sensitive of the two directions, since it hands whatever
        // the user last copied to whoever asked.
        Assert.True(PingPongHandler.RequiresPairing(MessageTypes.ClipboardRequest));
        Assert.True(PingPongHandler.RequiresPairing(MessageTypes.ClipboardContent));
    }

    [Fact]
    public void TheReasonVocabularyMatchesTheOneThePushDirectionUses()
    {
        // ONE SET OF TOKENS FOR ONE FEATURE. The phone maps these in one place; two vocabularies
        // would be two places to drift, and the fetch parser fails closed on anything it does not
        // recognise - so a token spelled differently here reads to the phone as a failure.
        var push = ClipboardValidation.ToNativeJson("");

        Assert.Contains("\"reason\":\"empty\"", push, StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"too_large\"",
            ClipboardValidation.ToNativeJson(new string('a', ClipboardValidation.MaxPayloadBytes + 1)),
            StringComparison.Ordinal);
        Assert.Contains("\"reason\":\"unavailable\"", ClipboardValidation.UnavailableNativeJson(), StringComparison.Ordinal);
    }
}
