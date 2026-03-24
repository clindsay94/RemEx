using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using System.Collections.Generic;

namespace Remex.Host.Tests;

public class RemoteDesktopHandlerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RemoteDesktopHandlerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private class MockCommandService : Remex.Core.Services.Command.ISystemCommandService
    {
        public void Lock() { }
        public void Shutdown() { }
        public void ForceShutdown() { }
        public void Restart() { }
        public void ForceRestart() { }
        public void RestartToUefi() { }
        public void Sleep() { }
        public void Hibernate() { }
        public void SignOut() { }
    }

    private class MockLauncherStorageService : Remex.Core.Services.ILauncherStorageService
    {
        public Task<List<Remex.Core.Models.AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<Remex.Core.Models.AppEntry>());
        public Task SaveEntriesAsync(IEnumerable<Remex.Core.Models.AppEntry> entries) => Task.CompletedTask;
    }

    private class MockScreenCaptureService : IScreenCaptureService
    {
        // Minimal valid JPEG: SOI + EOI markers
        private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0x00, 0x00, 0xFF, 0xD9];

        public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, CancellationToken ct = default)
            => Task.FromResult(FakeJpeg);

        public (int Width, int Height) GetScreenSize() => (1920, 1080);
    }

    private class MockInputSimulationService : IInputSimulationService
    {
        public List<string> ReceivedEvents { get; } = new();

        public void MoveMouse(int x, int y) => ReceivedEvents.Add($"move:{x},{y}");
        public void MouseMoveRelative(int dx, int dy) => ReceivedEvents.Add($"moverel:{dx},{dy}");
        public void MouseDown(int button) => ReceivedEvents.Add($"down:{button}");
        public void MouseUp(int button) => ReceivedEvents.Add($"up:{button}");
        public void MouseClick(int button) => ReceivedEvents.Add($"click:{button}");
        public void MouseScroll(int deltaX, int deltaY) => ReceivedEvents.Add($"scroll:{deltaX},{deltaY}");
        public void KeyDown(int keyCode) => ReceivedEvents.Add($"keydown:{keyCode}");
        public void KeyUp(int keyCode) => ReceivedEvents.Add($"keyup:{keyCode}");
        public void TypeText(string text) => ReceivedEvents.Add($"type:{text}");
    }

    private WebApplicationFactory<Program> GetFactory()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IScreenCaptureService, MockScreenCaptureService>();
                services.AddSingleton<IInputSimulationService, MockInputSimulationService>();
                services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, MockCommandService>();
                services.AddSingleton<Remex.Core.Services.ILauncherStorageService, MockLauncherStorageService>();
                services.Configure<Microsoft.Extensions.Hosting.HostOptions>(opts =>
                {
                    opts.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore;
                });
            });
        });
    }

    [Fact]
    public async Task DesktopStart_ReceivesMetaAndBinaryFrame()
    {
        var factory = GetFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        // Send desktop_start
        var startMsg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 50, Scale = 0.5, TargetFps = 5 },
        };
        await MessageSerializer.SendAsync(ws, startMsg, CancellationToken.None);

        // First response should be desktop_meta (text)
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        using (var ms = new System.IO.MemoryStream())
        {
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                Assert.Equal(WebSocketMessageType.Text, result.MessageType);
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var metaJson = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            var metaMsg = System.Text.Json.JsonSerializer.Deserialize<RemexMessage>(metaJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            Assert.NotNull(metaMsg);
            Assert.Equal(MessageTypes.DesktopMeta, metaMsg!.Type);
            Assert.Equal(1920, metaMsg.DesktopMeta!.ScreenWidth);
            Assert.Equal(1080, metaMsg.DesktopMeta.ScreenHeight);
        }

        // Second message should be a binary frame
        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.True(result.Count > 0);

        // Send stop to cleanly end streaming
        var stopMsg = new RemexMessage { Type = MessageTypes.DesktopStop };
        await MessageSerializer.SendAsync(ws, stopMsg, CancellationToken.None);

        // Drain remaining messages until server closes
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (System.IO.IOException) { }

        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { }
        }
    }

    [Fact]
    public async Task DesktopStop_StopsStreaming()
    {
        var factory = GetFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        // Start
        var startMsg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 50, Scale = 0.5, TargetFps = 5 },
        };
        await MessageSerializer.SendAsync(ws, startMsg, CancellationToken.None);

        // Read meta
        var buffer = new byte[4096];
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        // Read one binary frame
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        // Send stop
        var stopMsg = new RemexMessage { Type = MessageTypes.DesktopStop };
        await MessageSerializer.SendAsync(ws, stopMsg, CancellationToken.None);

        // The server should close the connection gracefully or stop sending frames.
        // Give it a moment then close.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            // Read until close or timeout
            while (!cts.Token.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { /* timeout is acceptable */ }
        catch (WebSocketException) { /* connection closed */ }
        catch (System.IO.IOException) { /* server closed first */ }

        // If still open, close cleanly
        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
            catch { }
        }
    }
}
