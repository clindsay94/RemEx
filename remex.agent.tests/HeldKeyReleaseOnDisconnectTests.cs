using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Session;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// End-to-end cover for the held-key release: dispatch key events, dispose the handler, assert the
/// right keyUps reach the host (RemEx-e2p4).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HeldKeyTrackerTests"/> pins the bookkeeping. This pins the WIRING, which is the part
/// that can be deleted with every other test still green — remove the <c>Pressed</c> call from
/// <c>DispatchInput</c>, or the release from <c>Dispose</c>, and the tracker's own tests do not
/// notice. That is the shape of RemEx-y6x6, where a stale allowlist silently dropped every v3 file
/// transfer and nothing failed.
/// </para>
/// <para>
/// The scenario is the bead's: a client tears the stream down mid-chord, so its closing keyUps never
/// arrive and the host is left holding modifiers on the user's real desktop.
/// </para>
/// </remarks>
public class HeldKeyReleaseOnDisconnectTests
{
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkC = 0x43;

    /// <summary>Records what the host was actually told to do, in order.</summary>
    /// <remarks>
    /// A Moq callback rather than a hand-written fake: <c>IInputSimulationService</c> has eleven
    /// members and this cares about two, so implementing it by hand would break on every unrelated
    /// addition to the interface for no benefit.
    /// </remarks>
    private sealed class Recorder
    {
        public List<(string Action, int KeyCode)> Events { get; } = [];

        public IInputSimulationService Build()
        {
            var mock = new Mock<IInputSimulationService>();
            mock.Setup(x => x.KeyDown(It.IsAny<int>())).Callback<int>(k => Events.Add(("down", k)));
            mock.Setup(x => x.KeyUp(It.IsAny<int>())).Callback<int>(k => Events.Add(("up", k)));
            return mock.Object;
        }
    }

    private static RemoteDesktopHandler NewHandler(Recorder recorder) =>
        new(
            NullLogger<RemoteDesktopHandler>.Instance,
            Mock.Of<IScreenCaptureService>(),
            recorder.Build(),
            Mock.Of<IDesktopWindowControlService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInteractiveSessionGuard>());

    private static InputEvent Key(string type, int keyCode) =>
        new() { EventType = type, KeyCode = keyCode };

    [Fact]
    public void DisconnectingMidChordReleasesTheModifiersTheClientLeftDown()
    {
        // THE BEAD. Ctrl+Shift+C where the client vanishes after the key's own keyUp but before the
        // modifiers'. Without the release, Ctrl and Shift stay down on the user's desktop and every
        // subsequent keystroke they type becomes a chord.
        var recorder = new Recorder();
        var handler = NewHandler(recorder);

        handler.DispatchInput(Key(InputEventTypes.KeyDown, VkControl));
        handler.DispatchInput(Key(InputEventTypes.KeyDown, VkShift));
        handler.DispatchInput(Key(InputEventTypes.KeyDown, VkC));
        handler.DispatchInput(Key(InputEventTypes.KeyUp, VkC));

        handler.Dispose();

        var released = recorder.Events.FindAll(e => e.Action == "up");
        Assert.Contains((("up"), VkControl), released);
        Assert.Contains((("up"), VkShift), released);
        Assert.Equal(3, released.Count); // the client's own keyUp for C, plus the two we recovered
    }

    [Fact]
    public void ACleanlyReleasedKeyIsNotReleasedTwice()
    {
        // The positive control, and it guards a real harm rather than a tidiness one: a spurious
        // keyUp at disconnect is a real key event delivered to whatever the user is doing at the PC.
        var recorder = new Recorder();
        var handler = NewHandler(recorder);

        handler.DispatchInput(Key(InputEventTypes.KeyDown, VkC));
        handler.DispatchInput(Key(InputEventTypes.KeyUp, VkC));

        handler.Dispose();

        Assert.Single(recorder.Events.FindAll(e => e.Action == "up"));
    }

    [Fact]
    public void ASessionWithNoKeyboardInputReleasesNothing()
    {
        // Most sessions are mouse-only. Disposing one must be silent — no synthetic keyUps at all.
        var recorder = new Recorder();
        var handler = NewHandler(recorder);

        handler.Dispose();

        Assert.Empty(recorder.Events);
    }
}
