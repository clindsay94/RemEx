using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Session;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;

namespace Remex.Agent.Tests;

/// <summary>
/// Failure and rejection paths of <see cref="RemoteDesktopHandler"/> that the end-to-end suite does not
/// reach: unavailable runtime, unsatisfiable capture targets, capture-backend failure, input hygiene and
/// independence of concurrent sessions (RemEx-uk38f.4). Authentication and pairing are enforced before
/// the handler runs (middleware), so they are not asserted here.
/// </summary>
public sealed class RemoteDesktopHandlerFailureTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly List<RemexMessage> _sent = [];
        private readonly object _gate = new();
        private WebSocketState _state = WebSocketState.Open;

        public WebSocketCloseStatus? ClosedWith { get; private set; }

        public void Receive(RemexMessage message) => _incoming.Writer.TryWrite(MessageSerializer.Serialize(message));

        // A client hanging up: the next receive reports a close frame.
        public void Disconnect() => _incoming.Writer.TryComplete();

        public List<RemexMessage> Sent
        {
            get { lock (_gate) return [.. _sent]; }
        }

        public async Task<RemexMessage> WaitForAsync(Func<RemexMessage, bool> match)
        {
            var deadline = DateTime.UtcNow + Budget;
            while (DateTime.UtcNow < deadline)
            {
                var hit = Sent.FirstOrDefault(match);
                if (hit is not null) return hit;
                await Task.Delay(10);
            }

            throw new TimeoutException("Expected message was never sent. Sent types: " + string.Join(",", Sent.Select(m => m.Type)));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
        {
            if (type == WebSocketMessageType.Text && endOfMessage)
            {
                var message = MessageSerializer.Deserialize(buffer.AsSpan());
                if (message is not null)
                    lock (_gate) _sent.Add(message);
            }

            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            try
            {
                var bytes = await _incoming.Reader.ReadAsync(ct);
                bytes.CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            }
            catch (ChannelClosedException)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "bye");
            }
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            ClosedWith = status;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
            => CloseOutputAsync(status, description, ct);

        public override WebSocketCloseStatus? CloseStatus => ClosedWith;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }

    private sealed class FakeCapture : IScreenCaptureService
    {
        public bool CaptureThrows { get; init; }
        public int Captures;
        public int WarmUps;
        public int TargetSets;

        public (int Width, int Height, int Left, int Top) Bounds { get; init; } = (1920, 1080, 0, 0);
        public int DisplayListVersion { get; init; } = 7;

        public Task<ReadOnlyMemory<byte>> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Captures);
            if (CaptureThrows) throw new InvalidOperationException("DXGI device lost");
            return Task.FromResult<ReadOnlyMemory<byte>>(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        }

        public (int Width, int Height, int Left, int Top) GetScreenSize() => Bounds;

        public void WarmUpCapture() => Interlocked.Increment(ref WarmUps);

        public DesktopDisplayCatalog GetDisplayCatalog() => new()
        {
            DisplayListVersion = DisplayListVersion,
            SupportedCaptureModes = [DesktopCaptureMode.VirtualDesktop, DesktopCaptureMode.Monitor],
            Displays =
            [
                new DesktopDisplayInfo
                {
                    DisplayId = "D1", PersistentDisplayKey = "D1", Name = "Display 1", IsPrimary = true,
                    Left = 0, Top = 0, Width = 1920, Height = 1080,
                },
            ],
        };

        public bool TrySetCaptureTarget(DesktopCaptureTarget target, out string? error)
        {
            Interlocked.Increment(ref TargetSets);
            if (target.CaptureMode == DesktopCaptureMode.Monitor && target.DisplayId != "D1")
            {
                error = "Unknown display.";
                return false;
            }

            error = null;
            return true;
        }
    }

    private static Mock<IHostCapabilitiesProvider> Host(bool supportsDesktop = true, string? reason = null)
    {
        var host = new Mock<IHostCapabilitiesProvider>();
        host.Setup(h => h.GetCurrent()).Returns(new HostCapabilities
        {
            SupportsRemoteDesktop = supportsDesktop,
            RemoteDesktopUnavailableReason = reason,
        });
        return host;
    }

    private static RemoteDesktopHandler NewHandler(
        IScreenCaptureService capture, IHostCapabilitiesProvider host, IInputSimulationService? input = null)
        => new(
            NullLogger<RemoteDesktopHandler>.Instance,
            capture,
            input ?? Mock.Of<IInputSimulationService>(),
            Mock.Of<IDesktopWindowControlService>(),
            host,
            Mock.Of<IInteractiveSessionGuard>());

    private static RemexMessage Start(DesktopConfig? config = null)
        => new() { Type = MessageTypes.DesktopStart, DesktopConfig = config };

    private static async Task CompletesWithinBudget(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(Budget));
        Assert.Same(task, finished);
        await task;
    }

    [Fact]
    public async Task UnavailableRuntime_ReportsCodedErrorWithReason_ClosesWithPolicyViolation_AndNeverTouchesCapture()
    {
        var capture = new FakeCapture();
        await using var handler = NewHandler(capture, Host(supportsDesktop: false, reason: "no interactive session").Object);
        var socket = new ScriptedSocket();
        // A start request is queued, but the handler must refuse before it ever reads the socket.
        socket.Receive(Start());

        await CompletesWithinBudget(handler.HandleAsync(socket, CancellationToken.None));

        var error = Assert.Single(socket.Sent);
        Assert.Equal(MessageTypes.DesktopError, error.Type);
        Assert.StartsWith(DesktopErrorCodes.RuntimeUnavailable, error.ErrorText);
        Assert.Contains("no interactive session", error.ErrorText);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.ClosedWith);
        Assert.Equal(0, capture.WarmUps);
        Assert.Equal(0, capture.Captures);
    }

    [Fact]
    public async Task UnavailableRuntime_WithoutReason_StillExplainsItself()
    {
        await using var handler = NewHandler(new FakeCapture(), Host(supportsDesktop: false).Object);
        var socket = new ScriptedSocket();

        await CompletesWithinBudget(handler.HandleAsync(socket, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(socket.Sent).ErrorText));
    }

    [Fact]
    public async Task DesktopStart_ForUnknownDisplay_IsRejectedAsTargetUnavailable_AndNoStreamStarts()
    {
        var capture = new FakeCapture();
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start(new DesktopConfig { CaptureMode = DesktopCaptureMode.Monitor, DisplayId = "GHOST" }));
        var error = await socket.WaitForAsync(m => m.Type == MessageTypes.DesktopError);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.StartsWith(DesktopErrorCodes.TargetUnavailable, error.ErrorText);
        Assert.Contains("Unknown display", error.ErrorText);
        Assert.Equal(0, capture.WarmUps);
        Assert.Equal(0, capture.Captures);
    }

    [Fact]
    public async Task DesktopStart_WithStaleDisplayListVersion_IsRejected_AndCaptureTargetIsNotChanged()
    {
        var capture = new FakeCapture { DisplayListVersion = 7 };
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start(new DesktopConfig
        {
            CaptureMode = DesktopCaptureMode.Monitor, DisplayId = "D1", DisplayListVersion = 6,
        }));
        var error = await socket.WaitForAsync(m => m.Type == MessageTypes.DesktopError);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.StartsWith(DesktopErrorCodes.TargetUnavailable, error.ErrorText);
        Assert.Contains("display list changed", error.ErrorText);
        Assert.Equal(0, capture.TargetSets);
        Assert.Equal(0, capture.WarmUps);
    }

    [Fact]
    public async Task DesktopStart_ExplicitTargetWithoutCaptureMode_IsRejected()
    {
        var capture = new FakeCapture();
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start(new DesktopConfig { DisplayId = "D1" }));
        var error = await socket.WaitForAsync(m => m.Type == MessageTypes.DesktopError);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.StartsWith(DesktopErrorCodes.TargetUnavailable, error.ErrorText);
        Assert.Equal(0, capture.WarmUps);
    }

    [Fact]
    public async Task DesktopStart_FromSelectionAwareClientWithoutATarget_IsRejected()
    {
        var capture = new FakeCapture();
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start(new DesktopConfig
        {
            DesktopProtocolVersion = 1,
            ClientCapabilities = new DesktopClientCapabilities { SupportsDisplaySelection = true },
        }));
        var error = await socket.WaitForAsync(m => m.Type == MessageTypes.DesktopError);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.Contains("Select a desktop target", error.ErrorText);
        Assert.Equal(0, capture.WarmUps);
    }

    [Fact]
    public async Task RejectedStart_LeavesTheConnectionUsable_ForALaterDisplayQuery()
    {
        await using var handler = NewHandler(new FakeCapture(), Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start(new DesktopConfig { CaptureMode = DesktopCaptureMode.Monitor, DisplayId = "GHOST" }));
        await socket.WaitForAsync(m => m.Type == MessageTypes.DesktopError);
        socket.Receive(new RemexMessage { Type = MessageTypes.DesktopDisplayQuery });
        var catalog = await socket.WaitForAsync(m => m.Type != MessageTypes.DesktopError);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.NotNull(catalog);
    }

    [Fact]
    public async Task MessagesBeforeStart_AreIgnoredWithoutErrorOrStream()
    {
        var capture = new FakeCapture();
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(new RemexMessage { Type = "totally_unknown_type" });
        socket.Receive(new RemexMessage { Type = MessageTypes.DesktopDisplayQuery });
        await socket.WaitForAsync(m => m.Type != MessageTypes.DesktopError);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.DoesNotContain(socket.Sent, m => m.Type == MessageTypes.DesktopError);
        Assert.Equal(0, capture.Captures);
    }

    [Fact]
    public async Task CaptureBackendFailing_SurfacesCaptureUnavailableToTheClient_ThenShutsDownCleanlyOnDisconnect()
    {
        var capture = new FakeCapture { CaptureThrows = true };
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start());
        var error = await socket.WaitForAsync(m => m.Type == MessageTypes.DesktopError);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.StartsWith(DesktopErrorCodes.CaptureUnavailable, error.ErrorText);
        Assert.True(capture.WarmUps >= 1);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedWith);
    }

    [Fact]
    public async Task CaptureBackendFailing_ReportsTheErrorOnce_NotOnEveryFailedFrame()
    {
        var capture = new FakeCapture { CaptureThrows = true };
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start());
        await socket.WaitForAsync(m => m.Type == MessageTypes.DesktopError);
        var failuresAtFirstReport = capture.Captures;
        // Wait for further failed captures to accumulate after the first report.
        var deadline = DateTime.UtcNow + Budget;
        while (capture.Captures < failuresAtFirstReport + 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        Assert.Equal(1, socket.Sent.Count(m => m.Type == MessageTypes.DesktopError));
    }

    [Fact]
    public async Task ClientDisconnectMidStream_StopsCapturing()
    {
        var capture = new FakeCapture();
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        var run = handler.HandleAsync(socket, CancellationToken.None);

        socket.Receive(Start());
        var deadline = DateTime.UtcNow + Budget;
        while (capture.Captures < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        socket.Disconnect();
        await CompletesWithinBudget(run);

        var afterStop = capture.Captures;
        await Task.Delay(50);
        Assert.True(afterStop >= 2);
        Assert.Equal(afterStop, capture.Captures);
    }

    [Fact]
    public async Task HostCancellationMidStream_EndsTheSessionWithoutThrowing()
    {
        var capture = new FakeCapture();
        await using var handler = NewHandler(capture, Host().Object);
        var socket = new ScriptedSocket();
        using var cts = new CancellationTokenSource();
        var run = handler.HandleAsync(socket, cts.Token);

        socket.Receive(Start());
        var deadline = DateTime.UtcNow + Budget;
        while (capture.Captures < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        cts.Cancel();

        await CompletesWithinBudget(run);
    }

    [Fact]
    public async Task TwoConcurrentSessions_AreIndependent_StoppingOneKeepsTheOtherStreaming()
    {
        var captureA = new FakeCapture();
        var captureB = new FakeCapture();
        await using var handlerA = NewHandler(captureA, Host().Object);
        await using var handlerB = NewHandler(captureB, Host().Object);
        var socketA = new ScriptedSocket();
        var socketB = new ScriptedSocket();
        var runA = handlerA.HandleAsync(socketA, CancellationToken.None);
        var runB = handlerB.HandleAsync(socketB, CancellationToken.None);

        socketA.Receive(Start());
        socketB.Receive(Start());
        var deadline = DateTime.UtcNow + Budget;
        while ((captureA.Captures < 1 || captureB.Captures < 1) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        socketA.Disconnect();
        await CompletesWithinBudget(runA);

        var bAfterA = captureB.Captures;
        deadline = DateTime.UtcNow + Budget;
        while (captureB.Captures < bAfterA + 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(captureB.Captures >= bAfterA + 2, "Session B stopped streaming when session A ended.");
        Assert.False(runB.IsCompleted);

        socketB.Disconnect();
        await CompletesWithinBudget(runB);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var handler = NewHandler(new FakeCapture(), Host().Object);

        await handler.DisposeAsync();
        await handler.DisposeAsync();
        handler.Dispose();
    }

    [Fact]
    public async Task DispatchInput_MouseMoveBeyondDisplay_IsClampedIntoTheActiveBounds()
    {
        var input = new Mock<IInputSimulationService>();
        var capture = new FakeCapture { Bounds = (1280, 1024, -1280, 0) };
        await using var handler = NewHandler(capture, Host().Object, input.Object);

        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseMove, X = 99999, Y = -500 });
        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseMove, X = -99999, Y = 5000 });

        input.Verify(i => i.MoveMouse(-1, 0), Times.Once);
        input.Verify(i => i.MoveMouse(-1280, 1023), Times.Once);
    }

    [Fact]
    public async Task DispatchInput_EventsMissingTheirRequiredFields_AreDroppedSilently()
    {
        var input = new Mock<IInputSimulationService>(MockBehavior.Strict);
        await using var handler = NewHandler(new FakeCapture(), Host().Object, input.Object);

        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.KeyDown });
        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.KeyUp });
        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseDown });
        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseClick });
        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseMove });
        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.TypeText });
        handler.DispatchInput(new InputEvent { EventType = "rm -rf" });
    }

    [Fact]
    public async Task DispatchInput_ClickWithCoordinates_MovesWithinBoundsBeforeClicking()
    {
        var calls = new List<string>();
        var input = new Mock<IInputSimulationService>();
        input.Setup(i => i.MoveMouse(It.IsAny<int>(), It.IsAny<int>())).Callback<int, int>((x, y) => calls.Add($"move:{x},{y}"));
        input.Setup(i => i.MouseClick(It.IsAny<int>())).Callback<int>(b => calls.Add($"click:{b}"));
        await using var handler = NewHandler(new FakeCapture(), Host().Object, input.Object);

        handler.DispatchInput(new InputEvent { EventType = InputEventTypes.MouseClick, Button = 1, X = 5000, Y = 5000 });

        Assert.Equal(new[] { "move:1919,1079", "click:1" }, calls);
    }
}
