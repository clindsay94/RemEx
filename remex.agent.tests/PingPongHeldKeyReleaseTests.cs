using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;
using Remex.Agent.Services.Security;

namespace Remex.Agent.Tests;

/// <summary>
/// Cover for the held-key release on the Remote Control (<c>/ws</c>) input path (RemEx-73dc).
/// </summary>
/// <remarks>
/// <para>
/// RemEx-e2p4 fixed this for the Remote Desktop stream. This is the other input path, which had the
/// same shape: a key press is two messages and a chord is six, so a client that vanishes between them
/// leaves the modifier physically held on the user's desktop.
/// </para>
/// <para>
/// NOTE WHAT THIS DOES AND DOES NOT COVER. No shipping client sends <c>desktop_input</c> over
/// <c>/ws</c> — the Android side routes it to <c>/ws/desktop</c>, so the Remote Control screen is
/// already covered by the Remote Desktop handler. This is defensive symmetry on a reachable protocol
/// branch that presses real keys, not a live user-facing bug.
/// </para>
/// <para>
/// These pin the WIRING rather than the bookkeeping — <see cref="HeldKeyTrackerTests"/> already
/// covers <c>HeldKeyTracker</c> itself. The wiring is what can be removed with every other test still
/// green, which is the shape of RemEx-y6x6.
/// </para>
/// <para>
/// The five concrete collaborators are passed as <c>null!</c> deliberately: a primary constructor
/// only captures what is used, and neither the key-dispatch path nor <c>Dispose</c> touches them.
/// Building real ones would mean a telemetry service, a pairing handler and a client registry for a
/// test about two integers. Same technique as the destructive-action tests (RemEx-w9ui).
/// </para>
/// </remarks>
public class PingPongHeldKeyReleaseTests
{
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkTab = 0x09;

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

    private static PingPongHandler NewHandler(Recorder recorder) =>
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
            recorder.Build(),
            null!,  // ScreenshotService: this test never takes one
            null!,
            null!,
            null!,
            null!,
            null!,  // FilePushOriginator: this test never pushes
            new Remex.Agent.Services.ClientSessionRegistry(),
            NewNameStore(),
            NewActivityStore(),
            new FakeHostClipboard());

    private static InputEvent Key(string type, int keyCode) =>
        new() { EventType = type, KeyCode = keyCode };

    [Fact]
    public void DisconnectingMidChordReleasesTheModifiersTheClientLeftDown()
    {
        // Alt+Tab where the phone dies after the Tab keyUp but before Alt's. Without the release,
        // Alt stays down on the user's desktop and the window switcher is stuck open.
        var recorder = new Recorder();
        var handler = NewHandler(recorder);

        handler.DispatchInput(Key(InputEventTypes.KeyDown, VkAlt));
        handler.DispatchInput(Key(InputEventTypes.KeyDown, VkTab));
        handler.DispatchInput(Key(InputEventTypes.KeyUp, VkTab));

        handler.Dispose();

        Assert.Contains(("up", VkAlt), recorder.Events);
    }

    [Fact]
    public void ACleanlyReleasedKeyIsNotReleasedTwice()
    {
        // A spurious keyUp at disconnect is a real key event delivered to whatever the user is doing
        // at the PC, so the common case must stay silent.
        var recorder = new Recorder();
        var handler = NewHandler(recorder);

        handler.DispatchInput(Key(InputEventTypes.KeyDown, VkControl));
        handler.DispatchInput(Key(InputEventTypes.KeyUp, VkControl));

        handler.Dispose();

        Assert.Single(recorder.Events.FindAll(e => e.Action == "up"));
    }

    [Fact]
    public void AConnectionThatSentNoKeysReleasesNothing()
    {
        // Most /ws connections never send a key at all — they are telemetry and commands. Disposing
        // one must emit nothing.
        var recorder = new Recorder();
        var handler = NewHandler(recorder);

        handler.Dispose();

        Assert.Empty(recorder.Events);
    }

    /// <summary>
    /// A name store pointed at a throwaway file.
    /// </summary>
    /// <remarks>
    /// NEVER the production path. That resolves to the machine-wide ProgramData store beside the
    /// pairing secrets, so a test constructing the default would read — and on any write, replace —
    /// the real device names on the developer's own machine.
    /// </remarks>
    private static PairedClientNameStore NewNameStore() =>
        new(NullLogger<PairedClientNameStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json"));

    /// <summary>A throwaway activity store; these tests never read a date back.</summary>
    private static PairedDeviceActivityStore NewActivityStore() =>
        new(NullLogger<PairedDeviceActivityStore>.Instance,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));

}
