using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Host.Services;
using Remex.Host.Services.Input;
using Remex.Host.Services.RemoteDesktop.Linux;
using Remex.Host.Services.RemoteDesktop.Windows;
using System.Collections.Generic;
using System.Linq;

namespace Remex.Host.Tests;

public class RemoteDesktopHandlerTests : IClassFixture<RemexHostFactory>
{
    private readonly RemexHostFactory _factory;

    public RemoteDesktopHandlerTests(RemexHostFactory factory)
    {
        _factory = factory;
    }

    private class MockCommandService : Remex.Core.Services.Command.ISystemCommandService
    {
        public Task Lock() => Task.CompletedTask;
        public Task Shutdown(int delaySeconds = 0) => Task.CompletedTask;
        public Task ForceShutdown(int delaySeconds = 0) => Task.CompletedTask;
        public Task Restart(int delaySeconds = 0) => Task.CompletedTask;
        public Task ForceRestart(int delaySeconds = 0) => Task.CompletedTask;
        public Task RestartToUefi(int delaySeconds = 0) => Task.CompletedTask;
        public Task Sleep() => Task.CompletedTask;
        public Task Hibernate() => Task.CompletedTask;
        public Task SignOut() => Task.CompletedTask;
        public Task MonitorOff() => Task.CompletedTask;
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
        private readonly DesktopDisplayCatalog _catalog =
            new()
            {
                DisplayListVersion = 42,
                SupportedCaptureModes = [DesktopCaptureMode.VirtualDesktop, DesktopCaptureMode.Monitor],
                Displays =
                [
                    new DesktopDisplayInfo
                    {
                        DisplayId = "DISPLAY1",
                        PersistentDisplayKey = "DISPLAY1",
                        Name = "Display 1",
                        IsPrimary = true,
                        Left = 0,
                        Top = 0,
                        Width = 1920,
                        Height = 1080,
                    },
                    new DesktopDisplayInfo
                    {
                        DisplayId = "DISPLAY2",
                        PersistentDisplayKey = "DISPLAY2",
                        Name = "Display 2",
                        IsPrimary = false,
                        Left = -1280,
                        Top = 0,
                        Width = 1280,
                        Height = 1024,
                    },
                ],
            };

        private DesktopCaptureTarget _target = new() { CaptureMode = DesktopCaptureMode.VirtualDesktop };

        public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(FakeJpeg);

        public DesktopDisplayCatalog GetDisplayCatalog() => _catalog;

        public bool TrySetCaptureTarget(DesktopCaptureTarget target, out string? error)
        {
            if (target.CaptureMode == DesktopCaptureMode.VirtualDesktop)
            {
                _target = target;
                error = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(target.DisplayId) ||
                !_catalog.Displays.Any(display => display.DisplayId == target.DisplayId))
            {
                error = "Unknown display.";
                return false;
            }

            _target = target;
            error = null;
            return true;
        }

        public (int Width, int Height, int Left, int Top) GetScreenSize()
        {
            if (_target.CaptureMode == DesktopCaptureMode.Monitor && !string.IsNullOrWhiteSpace(_target.DisplayId))
            {
                var display = _catalog.Displays.First(candidate => candidate.DisplayId == _target.DisplayId);
                return (display.Width, display.Height, display.Left, display.Top);
            }

            return (3200, 1080, -1280, 0);
        }
    }

    /// <summary>
    /// Returns a non-empty frame but reports <see cref="ScreenCaptureResult.IsLive"/> = false on every
    /// capture — simulating DXGI replaying its last good frame after the output was lost (ACCESS_LOST /
    /// session disconnect). Proves the handler stops counting a frozen output as a successful capture and
    /// emits a coded <c>desktop_error</c> instead of silently freezing the stream (RemEx-ltd).
    /// </summary>
    private sealed class StaleReplayScreenCaptureService : IScreenCaptureService
    {
        private static readonly byte[] FakeFrame = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0x00, 0x00, 0xFF, 0xD9];

        public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(FakeFrame);

        public Task<ScreenCaptureResult> CaptureScreenLiveAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(new ScreenCaptureResult(FakeFrame, isLive: false));

        public Task<ScreenCaptureResult> CaptureRawScreenLiveAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(new ScreenCaptureResult(FakeFrame, isLive: false));

        public (int Width, int Height, int Left, int Top) GetScreenSize() => (1920, 1080, 0, 0);
    }

    /// <summary>
    /// Reports the display as powered off (and, like a real powered-off DXGI output, would only return
    /// stale frames). Proves the handler pauses capture and emits NO <c>desktop_error</c> while the
    /// monitor is asleep — even though these stale frames would trip the coded capture-stopped error if
    /// the display were on (RemEx-960, defense-in-depth for RemEx-crk).
    /// </summary>
    private sealed class DisplayOffScreenCaptureService : IScreenCaptureService
    {
        private static readonly byte[] FakeFrame = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0x00, 0x00, 0xFF, 0xD9];

        public bool IsDisplayPoweredOff => true;

        public Task<byte[]> CaptureScreenAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(FakeFrame);

        public Task<ScreenCaptureResult> CaptureScreenLiveAsync(int quality = 50, double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(new ScreenCaptureResult(FakeFrame, isLive: false));

        public Task<ScreenCaptureResult> CaptureRawScreenLiveAsync(double scale = 1.0, bool drawCursor = true, CancellationToken ct = default)
            => Task.FromResult(new ScreenCaptureResult(FakeFrame, isLive: false));

        public (int Width, int Height, int Left, int Top) GetScreenSize() => (1920, 1080, 0, 0);
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
        public (int X, int Y) GetCursorPosition() => (0, 0);
    }

    private class MockHostCapabilitiesProvider : IHostCapabilitiesProvider
    {
        public HostCapabilities Current { get; init; } = new()
        {
            Platform = "test",
            SupportsRemoteDesktop = true,
            SupportsAdvancedWindowControl = true,
        };

        public LinuxPrerequisiteReport? LinuxReport { get; init; }
        public WindowsRemoteDesktopDiagnosticReport? WindowsReport { get; init; }

        public HostCapabilities GetCurrent() => Current;
        public LinuxPrerequisiteReport? GetLinuxPrerequisiteReport() => LinuxReport;
        public WindowsRemoteDesktopDiagnosticReport? GetWindowsRemoteDesktopDiagnosticReport() => WindowsReport;
    }

    private class MockDesktopWindowControlService : IDesktopWindowControlService
    {
        public DesktopWindowResult ExecuteAction(DesktopWindowAction action) => new()
        {
            RequestId = action.RequestId,
            Action = action.Action,
            Success = true,
            Backend = "mock",
        };

        public DesktopWindowResult QueryWindows(DesktopWindowQuery query) => new()
        {
            RequestId = query.RequestId,
            Success = true,
            Backend = "mock",
            CurrentDesktop = 1,
            DesktopCount = 2,
            Windows =
            [
                new DesktopWindowInfo
                {
                    Id = "window-1",
                    Title = "Test Window",
                    ClassName = "test",
                    DesktopNumber = 1,
                    IsActive = true,
                },
            ],
        };
    }

    private RemexHostFactory GetFactory(
        MockHostCapabilitiesProvider? hostCapabilitiesProvider = null,
        IScreenCaptureService? captureOverride = null)
    {
        hostCapabilitiesProvider ??= new MockHostCapabilitiesProvider();
        return new RemexHostFactory().WithServices(services =>
        {
            services.AddSingleton<IScreenCaptureService>(captureOverride ?? new MockScreenCaptureService());
            services.AddSingleton<MockInputSimulationService>();
            services.AddSingleton<IInputSimulationService>(sp => sp.GetRequiredService<MockInputSimulationService>());
            services.AddSingleton<Remex.Core.Services.Command.ISystemCommandService, MockCommandService>();
            services.AddSingleton<Remex.Core.Services.ILauncherStorageService, MockLauncherStorageService>();
            services.AddSingleton<IHostCapabilitiesProvider>(hostCapabilitiesProvider);
            services.AddSingleton<IDesktopWindowControlService, MockDesktopWindowControlService>();
            services.Configure<Microsoft.Extensions.Hosting.HostOptions>(opts =>
            {
                opts.BackgroundServiceExceptionBehavior = Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore;
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
            Assert.Equal(3200, metaMsg.DesktopMeta!.ScreenWidth);
            Assert.Equal(1080, metaMsg.DesktopMeta.ScreenHeight);
            Assert.Equal(-1280, metaMsg.DesktopMeta.DesktopLeft);
            Assert.Equal(0, metaMsg.DesktopMeta.DesktopTop);
        }

        // Second message is desktop_stream_descriptor (Stage 3)
        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);

        // Third message should be a binary frame
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
    public async Task DesktopDisplayQuery_ReturnsAvailableDisplays()
    {
        var factory = GetFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        await MessageSerializer.SendAsync(ws, new RemexMessage { Type = MessageTypes.DesktopDisplayQuery }, CancellationToken.None);

        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        using var ms = new System.IO.MemoryStream();
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var message = System.Text.Json.JsonSerializer.Deserialize<RemexMessage>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.NotNull(message);
        Assert.Equal(MessageTypes.DesktopDisplayList, message!.Type);
        Assert.NotNull(message.DesktopDisplayCatalog);
        Assert.Equal(42, message.DesktopDisplayCatalog!.DisplayListVersion);
        Assert.Equal(2, message.DesktopDisplayCatalog.Displays.Count);
        Assert.Contains(message.DesktopDisplayCatalog.SupportedCaptureModes, mode => mode == DesktopCaptureMode.VirtualDesktop);
        Assert.Contains(message.DesktopDisplayCatalog.SupportedCaptureModes, mode => mode == DesktopCaptureMode.Monitor);
    }

    [Fact]
    public async Task DesktopStart_WithExplicitDisplayTarget_UsesSelectedMonitorBounds()
    {
        var factory = GetFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        var startMsg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig
            {
                Quality = 50,
                Scale = 0.5,
                TargetFps = 5,
                DesktopProtocolVersion = 1,
                ClientCapabilities = new DesktopClientCapabilities
                {
                    SupportsDisplaySelection = true,
                },
                CaptureMode = DesktopCaptureMode.Monitor,
                DisplayId = "DISPLAY2",
                DisplayListVersion = 42,
            },
        };
        await MessageSerializer.SendAsync(ws, startMsg, CancellationToken.None);

        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        using var ms = new System.IO.MemoryStream();
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
        Assert.Equal(1280, metaMsg.DesktopMeta!.ScreenWidth);
        Assert.Equal(1024, metaMsg.DesktopMeta.ScreenHeight);
        Assert.Equal(-1280, metaMsg.DesktopMeta.DesktopLeft);
        Assert.Equal(0, metaMsg.DesktopMeta.DesktopTop);
    }

    [Fact]
    public async Task DesktopTargetSwitch_WithEnvelopeCapabilities_SendsNewStreamMetadata()
    {
        var factory = GetFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig
            {
                Quality = 50,
                Scale = 0.5,
                TargetFps = 5,
                DesktopProtocolVersion = 1,
                ClientCapabilities = new DesktopClientCapabilities
                {
                    SupportsDisplaySelection = true,
                    SupportsFrameEnvelope = true,
                    SupportsTargetSwitch = true,
                    SupportsCursorState = true,
                },
                CaptureMode = DesktopCaptureMode.VirtualDesktop,
                DisplayListVersion = 42,
            },
        }, CancellationToken.None);

        var initialMeta = await ReceiveTextMessageAsync(ws, CancellationToken.None);
        Assert.Equal(MessageTypes.DesktopMeta, initialMeta.Type);
        var initialSerial = initialMeta.DesktopMeta!.StreamSerial;

        var descriptor = await ReceiveTextMessageAsync(ws, CancellationToken.None);
        Assert.Equal(MessageTypes.DesktopStreamDescriptor, descriptor.Type);
        Assert.Equal(initialSerial, descriptor.DesktopStreamDescriptor!.StreamSerial);

        var cursorState = await ReceiveTextMessageAsync(ws, CancellationToken.None);
        Assert.Equal(MessageTypes.DesktopCursorState, cursorState.Type);
        Assert.Equal(initialSerial, cursorState.DesktopCursorState!.StreamSerial);

        var initialFrame = await ReceiveBinaryMessageAsync(ws, CancellationToken.None);
        Assert.True(DesktopFrameEnvelope.TryRead(initialFrame, out var initialHeader, out _));
        Assert.Equal(initialSerial, initialHeader.StreamSerial);

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.DesktopTargetSwitch,
            DesktopTargetSwitch = new DesktopTargetSwitchRequest
            {
                Target = new DesktopCaptureTarget
                {
                    CaptureMode = DesktopCaptureMode.Monitor,
                    DisplayId = "DISPLAY2",
                },
                DisplayListVersion = 42,
            },
        }, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RemexMessage? switchedMeta = null;
        while (!cts.IsCancellationRequested)
        {
            var (messageType, payload) = await ReceiveSocketMessageAsync(ws, cts.Token);
            if (messageType == WebSocketMessageType.Text)
            {
                var message = DeserializeRemexMessage(payload);
                if (message?.Type == MessageTypes.DesktopMeta &&
                    message.DesktopMeta is not null &&
                    message.DesktopMeta.StreamSerial != initialSerial)
                {
                    switchedMeta = message;
                    break;
                }
            }
        }

        Assert.NotNull(switchedMeta);
        Assert.Equal(1280, switchedMeta!.DesktopMeta!.ScreenWidth);
        Assert.Equal(1024, switchedMeta.DesktopMeta.ScreenHeight);
        Assert.Equal(-1280, switchedMeta.DesktopMeta.DesktopLeft);
        Assert.NotEqual(initialSerial, switchedMeta.DesktopMeta.StreamSerial);
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

    [Fact]
    public async Task DesktopWindowQuery_ReturnsStructuredResult()
    {
        var factory = GetFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        var startMsg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 50, Scale = 0.5, TargetFps = 5 },
        };
        await MessageSerializer.SendAsync(ws, startMsg, CancellationToken.None);

        var buffer = new byte[4096];
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); // desktop_meta
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); // desktop_stream_descriptor (Stage 3)
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); // first binary frame

        var queryMsg = new RemexMessage
        {
            Type = MessageTypes.DesktopWindowQuery,
            DesktopWindowQuery = new DesktopWindowQuery
            {
                RequestId = "query-1",
                SearchText = "Test",
            },
        };

        await MessageSerializer.SendAsync(ws, queryMsg, CancellationToken.None);

        using var ms = new System.IO.MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                continue;
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        var response = System.Text.Json.JsonSerializer.Deserialize<RemexMessage>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.NotNull(response);
        Assert.Equal(MessageTypes.DesktopWindowResult, response!.Type);
        Assert.NotNull(response.DesktopWindowResult);
        Assert.True(response.DesktopWindowResult!.Success);
        Assert.Equal("query-1", response.DesktopWindowResult.RequestId);
        Assert.Single(response.DesktopWindowResult.Windows!);
        Assert.Equal("Test Window", response.DesktopWindowResult.Windows![0].Title);

        await MessageSerializer.SendAsync(ws, new RemexMessage { Type = MessageTypes.DesktopStop }, CancellationToken.None);
    }

    [Fact]
    public async Task ExternalCtsCancel_ExitsStreamLoopWithin200ms()
    {
        // Arrange: wire up a factory with the mock screen capture service.
        var factory = GetFactory();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        // Start streaming
        var startMsg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 50, Scale = 0.5, TargetFps = 30 },
        };
        await MessageSerializer.SendAsync(ws, startMsg, CancellationToken.None);

        // Drain meta + descriptor + at least one binary frame so the loop is running.
        var buffer = new byte[4096];
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); // desktop_meta
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); // first frame or descriptor

        // Act: close the underlying connection abruptly (simulates TCP FIN from Android reconnect).
        // The registry cancels the handler's CTS — we replicate that by simply closing the socket.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ws.Abort(); // forces WebSocketState to Aborted, unblocking the server-side loop

        // Allow the server-side loop to drain.
        await Task.Delay(200);
        sw.Stop();

        // Assert: the abort + 200ms window should be well within the 200ms budget.
        // The real invariant tested: the handler loop does NOT hang after socket closure.
        Assert.True(sw.ElapsedMilliseconds < 600,
            $"Handler loop should unblock within 600ms of socket abort, but test took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task DesktopInput_MouseClickWithoutCoordinates_DoesNotMoveCursor()
    {
        var factory = GetFactory();
        var input = factory.Services.GetRequiredService<MockInputSimulationService>();
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        var startMsg = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 50, Scale = 0.5, TargetFps = 5 },
        };
        await MessageSerializer.SendAsync(ws, startMsg, CancellationToken.None);

        var buffer = new byte[4096];
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); // desktop_meta
        await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None); // first frame

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = new InputEvent
            {
                EventType = InputEventTypes.MouseClick,
                Button = 0,
            },
        }, CancellationToken.None);

        await Task.Delay(100);

        Assert.Contains("click:0", input.ReceivedEvents);
        Assert.DoesNotContain(input.ReceivedEvents, evt => evt.StartsWith("move:", StringComparison.Ordinal));

        await MessageSerializer.SendAsync(ws, new RemexMessage { Type = MessageTypes.DesktopStop }, CancellationToken.None);
    }

    [Fact]
    public async Task DesktopStart_WhenWindowsDesktopBlocked_SendsActionableDesktopError()
    {
        var factory = GetFactory(new MockHostCapabilitiesProvider
        {
            Current = new HostCapabilities
            {
                Platform = "windows",
                SupportsRemoteDesktop = true,
                SupportsAdvancedWindowControl = true,
            },
            WindowsReport = new WindowsRemoteDesktopDiagnosticReport
            {
                SupportsRemoteDesktopSession = true,
                CurrentDesktopReady = false,
                InputDesktopAccessible = true,
                InputDesktopName = "Winlogon",
                CurrentDesktopUnavailableReason = "Windows is currently showing the Winlogon secure desktop (lock screen or credential/UAC prompt). Unlock the session or dismiss the secure prompt, then retry remote desktop.",
                Issues =
                [
                    "Windows is currently showing the Winlogon secure desktop (lock screen or credential/UAC prompt). Unlock the session or dismiss the secure prompt, then retry remote desktop.",
                ],
            },
        });

        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig(),
        }, CancellationToken.None);

        var message = await MessageSerializer.ReceiveAsync(ws, CancellationToken.None);
        Assert.NotNull(message);
        Assert.Equal(MessageTypes.DesktopError, message!.Type);
        Assert.Contains("Winlogon", message.ErrorText);
        Assert.Contains("Unlock the session", message.ErrorText);
    }

    [Fact]
    public async Task DesktopStart_WhenCaptureReplaysStaleFrames_SendsCodedCaptureError()
    {
        // Capture output is "frozen": every tick returns a non-empty but STALE (IsLive=false) frame, as
        // DXGI does when it replays its last good frame after ACCESS_LOST. The handler must stop counting
        // these as successful captures and emit a coded desktop_error rather than silently freezing the
        // stream while the cursor (a separate stream) keeps moving (RemEx-ltd).
        var factory = GetFactory(captureOverride: new StaleReplayScreenCaptureService());
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 50, Scale = 0.5, TargetFps = 30 },
        }, CancellationToken.None);

        // desktop_meta and desktop_stream_descriptor precede the error; no binary frame should ever arrive
        // on a frozen output. Read text messages until the coded capture error appears (bounded timeout).
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        RemexMessage? errorMessage = null;
        try
        {
            while (errorMessage is null)
            {
                var (messageType, payload) = await ReceiveSocketMessageAsync(ws, cts.Token);
                if (messageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var message = DeserializeRemexMessage(payload);
                if (message?.Type == MessageTypes.DesktopError)
                {
                    errorMessage = message;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through to the assertion, which fails clearly if no coded error arrived in time.
        }

        Assert.NotNull(errorMessage);
        Assert.NotNull(errorMessage!.ErrorText);
        // Coded path: capture never produced a live frame, so the host reports CaptureUnavailable. The code
        // travels inside errorText as "code␟arg␟englishFallback" (DesktopErrorCodes.Format), so a substring
        // check recovers it without coupling to the human-readable fallback.
        Assert.Contains(DesktopErrorCodes.CaptureUnavailable, errorMessage.ErrorText!);

        await MessageSerializer.SendAsync(ws, new RemexMessage { Type = MessageTypes.DesktopStop }, CancellationToken.None);
    }

    [Fact]
    public async Task DesktopStart_WhenDisplayPoweredOff_PausesWithoutCaptureError()
    {
        // Display asleep: the handler must pause capture and emit NO desktop_error, even though the
        // underlying capture returns stale frames that — with the display ON — trip the coded
        // capture-stopped error (see DesktopStart_WhenCaptureReplaysStaleFrames_SendsCodedCaptureError).
        var factory = GetFactory(captureOverride: new DisplayOffScreenCaptureService());
        var wsClient = factory.Server.CreateWebSocketClient();
        var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/ws/desktop"), CancellationToken.None);

        await MessageSerializer.SendAsync(ws, new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 50, Scale = 0.5, TargetFps = 30 },
        }, CancellationToken.None);

        // Watch for ~2.5 s — far longer than the <0.3 s a non-paused stale-replay path takes to reach
        // the 5-failure error at 30 fps. No desktop_error and no binary frame should ever arrive.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        var sawError = false;
        var sawBinaryFrame = false;
        try
        {
            while (true)
            {
                var (messageType, payload) = await ReceiveSocketMessageAsync(ws, cts.Token);
                if (messageType == WebSocketMessageType.Binary)
                {
                    sawBinaryFrame = true;
                    continue;
                }

                var message = DeserializeRemexMessage(payload);
                if (message?.Type == MessageTypes.DesktopError)
                {
                    sawError = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected success path: the stream idles, so we time out with no error.
        }

        Assert.False(sawError, "A powered-off display must not surface a capture error.");
        Assert.False(sawBinaryFrame, "A powered-off display must not stream frames.");

        await MessageSerializer.SendAsync(ws, new RemexMessage { Type = MessageTypes.DesktopStop }, CancellationToken.None);
    }

    private static async Task<RemexMessage> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken ct)
    {
        var (messageType, payload) = await ReceiveSocketMessageAsync(webSocket, ct);
        Assert.Equal(WebSocketMessageType.Text, messageType);
        var message = DeserializeRemexMessage(payload);
        Assert.NotNull(message);
        return message!;
    }

    private static async Task<byte[]> ReceiveBinaryMessageAsync(WebSocket webSocket, CancellationToken ct)
    {
        var (messageType, payload) = await ReceiveSocketMessageAsync(webSocket, ct);
        Assert.Equal(WebSocketMessageType.Binary, messageType);
        return payload;
    }

    private static RemexMessage? DeserializeRemexMessage(byte[] payload) =>
        System.Text.Json.JsonSerializer.Deserialize<RemexMessage>(
            payload,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

    private static async Task<(WebSocketMessageType MessageType, byte[] Payload)> ReceiveSocketMessageAsync(WebSocket webSocket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        using var ms = new System.IO.MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (result.MessageType, ms.ToArray());
    }
}
