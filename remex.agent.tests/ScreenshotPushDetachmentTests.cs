using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Remex.Agent.Handlers;
using Remex.Agent.Services;
using Remex.Agent.Services.FileTransfer;
using Remex.Agent.Services.Screenshot;
using Remex.Agent.Services.Security;
using Remex.Agent.Services.Telemetry;
using Remex.Core.Messages;
using Remex.Core.Services;
using Remex.Core.Services.FileTransfer;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the SCREENSHOT command's response does not wait for the file push (RemEx-hn23).
/// </summary>
/// <remarks>
/// <para>
/// THE BUG THIS GUARDS AGAINST SHIPPED. The first version of RemEx-y7my awaited the push inline in
/// <c>ExecuteCommandAsync</c>, which runs inside the single control-socket reader loop — and the
/// phone's answer to the offer arrives ON THAT SOCKET. The command therefore waited for a message
/// only its own caller could read: it timed out after the full 70-second offer window every time,
/// and every other inbound message stalled behind it meanwhile. The fix is the
/// <c>_ = RunDetachedAsync(...)</c> dispatch at the SCREENSHOT case; this test is the reason a
/// future tidy-up cannot quietly turn that back into an <c>await</c>.
/// </para>
/// <para>
/// THE PROPERTY IS TIMING, DELIBERATELY MEASURED WITH A 6x MARGIN. The originator's
/// <c>OfferTimeout</c> is pinned at 60 seconds and the response is given 10: detached code answers
/// in milliseconds, inline-awaited code cannot answer before the offer times out, and nothing in
/// between exists. The push arm is proven live rather than assumed — the offer frame must actually
/// reach the wire, on a socket that never answers it, so a mutation that skips the push entirely
/// (which would also produce a fast response) fails the second assertion instead of passing the
/// first.
/// </para>
/// <para>
/// <c>ExecuteCommandAsync</c> is driven directly through its internal seam rather than through
/// <c>HandleAsync</c>: <c>command</c> is pairing-gated for remote connections and loopback is
/// identity-frozen (RemEx-4215), so reaching this state through the loop needs a real pairing
/// exchange — the seam's <c>clientId</c> parameter arms the push without one. Collaborators are
/// mocked, real, or <c>null!</c> by whether the SCREENSHOT case reaches them, the same technique as
/// <see cref="LoopbackTelemetryStreamTests"/>.
/// </para>
/// </remarks>
public class ScreenshotPushDetachmentTests
{
    private const string PhoneClientId = "phone-under-test";

    /// <summary>
    /// Records everything the host sends and never answers any of it — the pending offer this
    /// leaves behind is the point of the test.
    /// </summary>
    private sealed class AnswerlessWebSocket : WebSocket
    {
        public List<RemexMessage> Sent { get; } = [];

        public TaskCompletionSource OfferOnTheWire { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType t, bool e, CancellationToken c)
        {
            var message = MessageSerializer.Deserialize(buffer.AsSpan());
            if (message is not null)
            {
                lock (Sent) Sent.Add(message);
                if (message.Type == MessageTypes.FilePushOffer) OfferOnTheWire.TrySetResult();
            }

            return Task.CompletedTask;
        }

        // Only the reader loop receives, and this test never runs it.
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c) =>
            throw new InvalidOperationException("This test drives ExecuteCommandAsync directly; nothing should receive.");

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
    }

    private static PingPongHandler NewHandler(string screenshotPath, FilePushOriginator originator)
    {
        var screenshots = new Mock<IScreenshotService>();
        screenshots
            .Setup(s => s.CaptureAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(screenshotPath);

        return new PingPongHandler(
            NullLogger<PingPongHandler>.Instance,
            new TelemetryBackgroundService(
                Mock.Of<ITelemetryService>(), NullLogger<TelemetryBackgroundService>.Instance),
            Mock.Of<Remex.Core.Services.Command.ISystemCommandService>(),
            Mock.Of<Remex.Core.Services.Network.IWakeOnLanService>(),
            Mock.Of<ILauncherStorageService>(),
            Mock.Of<IAppLauncherService>(),
            Mock.Of<IDashboardProfileStorageService>(),
            Mock.Of<IProcessMonitorService>(),
            Mock.Of<IHostCapabilitiesProvider>(),
            Mock.Of<IInputSimulationService>(),
            screenshots.Object,
            null!,  // PairingHandler: the SCREENSHOT case never pairs
            null!,  // FileTransferHandler: only the reader loop's cleanup touches it, and the loop never runs
            null!,  // TransferSessionManager: reached only AFTER the offer is accepted, and nothing here accepts
            null!,  // PairedClientRegistry: only the reconnect challenge reads it
            originator,
            new ClientSessionRegistry(),
            new PairedClientNameStore(
                NullLogger<PairedClientNameStore>.Instance,
                Path.Combine(Path.GetTempPath(), $"remex-names-{Guid.NewGuid():N}.json")),
            new PairedDeviceActivityStore(
                NullLogger<PairedDeviceActivityStore>.Instance,
                Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())),
            new FakeHostClipboard(),
            Mock.Of<Remex.Agent.Services.Media.IMediaSessionMonitor>());
    }

    [Fact]
    public async Task TheScreenshotResponseDoesNotWaitForThePush()
    {
        var shot = Path.Combine(Path.GetTempPath(), $"remex-shot-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(shot, new byte[64]);
        try
        {
            var socket = new AnswerlessWebSocket();

            // The window an inline-awaited push would have to sit out before it could respond. Long
            // enough that the 10-second response deadline below cannot be beaten by the timeout path.
            var originator = new FilePushOriginator(NullLogger<FilePushOriginator>.Instance)
            {
                OfferTimeout = TimeSpan.FromSeconds(60),
            };

            using var handler = NewHandler(shot, originator);

            var respond = handler.ExecuteCommandAsync(
                new RemexMessage { Type = MessageTypes.Command, CommandAction = "SCREENSHOT" },
                socket, PhoneClientId, CancellationToken.None);

            // Detached: milliseconds. Inline-awaited: not before the 60s offer timeout. There is no
            // code path that answers in between, so 10 seconds separates them without flake risk.
            var winner = await Task.WhenAny(respond, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(respond, winner);

            var response = await respond;
            Assert.Equal(MessageTypes.CommandResponse, response.Type);
            Assert.True(response.CommandSuccess == true,
                "the response must report the capture, which succeeded before the push began");

            // THE PUSH REALLY STARTED — this is what stops a mutation that deletes the push (also
            // fast) from passing. The offer must reach the wire, and this socket never answers it,
            // so the response above was produced while the offer was still pending by construction.
            await socket.OfferOnTheWire.Task.WaitAsync(TimeSpan.FromSeconds(10));

            RemexMessage[] sent;
            lock (socket.Sent) sent = [.. socket.Sent];
            var offer = Assert.Single(sent, m => m.Type == MessageTypes.FilePushOffer);
            Assert.NotNull(offer.FilePushOffer);
        }
        finally
        {
            File.Delete(shot);
        }
    }
}
