using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Media;
using Remex.Agent.Services.Security;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// <c>PingPongHandler</c>'s input queue (perf audit P1-4): a hung input backend used to stall the
/// whole control socket, because <c>DispatchInput</c> ran inline in the receive loop. It now runs on
/// its own consumer thread, fed by a <c>BlockingCollection</c>.
/// </summary>
/// <remarks>
/// Driven through the real receive loop with a scripted <see cref="WebSocket"/>, the same shape
/// <c>MediaSeekDispatchTests</c> uses — the property under test (a blocked input call must not delay
/// the reply to an UNRELATED later message) only exists at that level; calling a method directly
/// would prove nothing about the receive loop itself.
/// </remarks>
public class PingPongInputOffloadTests
{
    /// <summary>Delivers a scripted list of messages, one per receive, then closes.</summary>
    private sealed class ScriptedWebSocket(params RemexMessage[] script) : WebSocket
    {
        private int _next;

        public List<RemexMessage> Sent { get; } = [];

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null)
            {
                lock (Sent) Sent.Add(message);
            }

            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c)
        {
            if (_next >= script.Length)
            {
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            var payload = MessageSerializer.Serialize(script[_next++]);
            payload.CopyTo(b.Array.AsSpan(b.Offset));
            return Task.FromResult(
                new WebSocketReceiveResult(payload.Length, WebSocketMessageType.Text, true));
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
    }

    [Fact]
    public async Task APongForALaterMessage_IsSent_WhileAnEarlierInputEventIsStillDispatching()
    {
        // The scenario RemoteDesktopHandler already solved and PingPongHandler didn't, until this
        // row: a slow (here, deliberately blocked) input call must not delay ANYTHING queued behind
        // it on the wire — a ping, a file-transfer message, another connection's traffic. Before this
        // fix, DispatchInput ran inline, so the receive loop itself could not reach the Ping case
        // until KeyDown returned.
        // MUST run HandleAsync on its own thread (Task.Run), not just call it: every await in it
        // completes synchronously against a scripted socket, so calling it directly would run the
        // whole receive loop, including a 10s-blocked KeyDown, on THIS thread — with nothing yielded
        // back, a Pong-arrived assertion could not even be checked until AFTER KeyDown unblocks,
        // which would defeat the entire point of this test (round-2 review finding).
        var keyDownStarted = new SemaphoreSlim(0);
        var releaseKeyDown = new SemaphoreSlim(0);
        var input = new Mock<IInputSimulationService>();
        input.Setup(i => i.KeyDown(It.IsAny<int>())).Callback<int>(_ =>
        {
            keyDownStarted.Release();
            releaseKeyDown.Wait(TimeSpan.FromSeconds(30));
        });

        var socket = new ScriptedWebSocket(
            DesktopKeyDown(65),
            new RemexMessage { Type = MessageTypes.Ping, Timestamp = 123 });
        var handler = NewHandler(input.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var handleTask = Task.Run(() => handler.HandleAsync(
            socket, isLoopback: true, isTrustedForPinAutoFetch: false, remoteAddress: "127.0.0.1", cts.Token));

        try
        {
            Assert.True(keyDownStarted.Wait(TimeSpan.FromSeconds(10)),
                "KeyDown must have started on the consumer thread");

            // KeyDown is still blocked at this point. The receive loop must already have moved past
            // the input message, reached the Ping, and sent the Pong — proving dispatch happened
            // off-loop. A short window: the OLD inline-dispatch code cannot send the Pong until
            // KeyDown returns (30s later), so this must fail promptly against it, not eventually pass.
            Assert.True(await WaitUntilAsync(() => HasSent(socket, MessageTypes.Pong), TimeSpan.FromSeconds(3)),
                "a Pong for the message queued AFTER the blocked input event must arrive without waiting for it");
        }
        finally
        {
            releaseKeyDown.Release();
        }

        await handleTask;
        handler.Dispose();
    }

    [Fact]
    public async Task QueuedInputEvents_DispatchInTheOrderTheyArrived()
    {
        var dispatched = new List<int>();
        var input = new Mock<IInputSimulationService>();
        input.Setup(i => i.KeyDown(It.IsAny<int>())).Callback<int>(code =>
        {
            lock (dispatched) dispatched.Add(code);
        });
        input.Setup(i => i.KeyUp(It.IsAny<int>())).Callback<int>(code =>
        {
            lock (dispatched) dispatched.Add(-code); // KeyUp recorded as the negative code
        });

        // Balanced Down/Up pairs per key: nothing is left held at teardown, so Dispose's own
        // held-key release (RemEx-73dc) contributes no extra events to compare against.
        const int pairs = 25;
        var script = new RemexMessage[pairs * 2];
        var expected = new List<int>();
        for (var i = 0; i < pairs; i++)
        {
            var code = i + 1; // avoid 0: -0 == 0 makes Down/Up indistinguishable in the recorded list
            script[i * 2] = DesktopKeyDown(code);
            script[i * 2 + 1] = DesktopKeyUp(code);
            expected.Add(code);
            expected.Add(-code);
        }

        var socket = new ScriptedWebSocket(script);
        var handler = NewHandler(input.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await handler.HandleAsync(
            socket, isLoopback: true, isTrustedForPinAutoFetch: false, remoteAddress: "127.0.0.1", cts.Token);
        handler.Dispose();

        Assert.Equal(expected, dispatched);
    }

    private static bool HasSent(ScriptedWebSocket socket, string type)
    {
        lock (socket.Sent) return socket.Sent.Any(m => m.Type == type);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }

        return condition();
    }

    private static RemexMessage DesktopKeyDown(int keyCode) => new()
    {
        Type = MessageTypes.DesktopInput,
        InputEvent = new InputEvent { EventType = InputEventTypes.KeyDown, KeyCode = keyCode },
    };

    private static RemexMessage DesktopKeyUp(int keyCode) => new()
    {
        Type = MessageTypes.DesktopInput,
        InputEvent = new InputEvent { EventType = InputEventTypes.KeyUp, KeyCode = keyCode },
    };

    /// <summary>Same collaborator shape as <c>MediaSeekDispatchTests.RunAsync</c>: null! where this
    /// path never reaches them, the two exceptions being the file-transfer handler (teardown calls it
    /// on every exit path) and the session registry (registered on HandleAsync's first line).</summary>
    private static PingPongHandler NewHandler(IInputSimulationService input) => new(
        NullLogger<PingPongHandler>.Instance,
        null!,
        Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
        Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
        Mock.Of<ILauncherStorageService>(),
        Mock.Of<IAppLauncherService>(),
        Mock.Of<IDashboardProfileStorageService>(),
        Mock.Of<IProcessMonitorService>(),
        Mock.Of<IHostCapabilitiesProvider>(),
        input,
        null!,
        null!,
        NewFileTransferHandler(),
        null!,
        null!,
        null!,
        new ClientSessionRegistry(),
        NewNameStore(),
        NewActivityStore(),
        new FakeHostClipboard(),
        Mock.Of<IMediaSessionMonitor>(),
        new Remex.Agent.Services.Theme.PhoneThemeSnapshotStore());

    private static FileTransferHandler NewFileTransferHandler()
    {
        var svc = Mock.Of<Remex.Core.Services.FileTransfer.IFileTransferService>();
        var trust = Mock.Of<Remex.Core.Services.FileTransfer.IFileTrustService>();
        var volumes = new Remex.Agent.Services.FileTransfer.VolumeEnumerator(
            NullLogger<Remex.Agent.Services.FileTransfer.VolumeEnumerator>.Instance);
        return new FileTransferHandler(
            NullLogger<FileTransferHandler>.Instance, svc, trust, volumes,
            new Remex.Agent.Services.FileTransfer.SharedRootReadResolver(svc, trust, volumes));
    }

    private static PairedClientNameStore NewNameStore() =>
        new(NullLogger<PairedClientNameStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json"));

    private static PairedDeviceActivityStore NewActivityStore() =>
        new(NullLogger<PairedDeviceActivityStore>.Instance,
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
}
