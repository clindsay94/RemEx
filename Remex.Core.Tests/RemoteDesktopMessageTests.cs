using Remex.Core.Messages;
using Remex.Core.Models;

namespace Remex.Core.Tests;

public class RemoteDesktopMessageTests
{
    [Fact]
    public void RoundTrip_DesktopStartWithConfig_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopStart,
            DesktopConfig = new DesktopConfig { Quality = 75, Scale = 0.5, TargetFps = 15 }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopStart, deserialized!.Type);
        Assert.NotNull(deserialized.DesktopConfig);
        Assert.Equal(75, deserialized.DesktopConfig!.Quality);
        Assert.Equal(0.5, deserialized.DesktopConfig.Scale);
        Assert.Equal(15, deserialized.DesktopConfig.TargetFps);
    }

    [Fact]
    public void RoundTrip_DesktopMetaMessage_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopMeta,
            DesktopMeta = new DesktopMeta { ScreenWidth = 1920, ScreenHeight = 1080 }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopMeta, deserialized!.Type);
        Assert.NotNull(deserialized.DesktopMeta);
        Assert.Equal(1920, deserialized.DesktopMeta!.ScreenWidth);
        Assert.Equal(1080, deserialized.DesktopMeta.ScreenHeight);
    }

    [Fact]
    public void RoundTrip_DesktopInputMouseMove_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = new InputEvent
            {
                EventType = InputEventTypes.MouseMove,
                X = 500,
                Y = 300,
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopInput, deserialized!.Type);
        Assert.NotNull(deserialized.InputEvent);
        Assert.Equal(InputEventTypes.MouseMove, deserialized.InputEvent!.EventType);
        Assert.Equal(500, deserialized.InputEvent.X);
        Assert.Equal(300, deserialized.InputEvent.Y);
    }

    [Fact]
    public void RoundTrip_DesktopInputKeyDown_PreservesKeyCode()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = new InputEvent
            {
                EventType = InputEventTypes.KeyDown,
                KeyCode = 65, // 'A'
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.InputEvent);
        Assert.Equal(InputEventTypes.KeyDown, deserialized.InputEvent!.EventType);
        Assert.Equal(65, deserialized.InputEvent.KeyCode);
    }

    [Fact]
    public void RoundTrip_DesktopInputMouseScroll_PreservesDelta()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopInput,
            InputEvent = new InputEvent
            {
                EventType = InputEventTypes.MouseScroll,
                DeltaX = 0,
                DeltaY = -120,
            }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.InputEvent);
        Assert.Equal(InputEventTypes.MouseScroll, deserialized.InputEvent!.EventType);
        Assert.Equal(0, deserialized.InputEvent.DeltaX);
        Assert.Equal(-120, deserialized.InputEvent.DeltaY);
    }

    [Fact]
    public void RoundTrip_DesktopStopMessage()
    {
        var original = new RemexMessage { Type = MessageTypes.DesktopStop };
        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopStop, deserialized!.Type);
    }

    [Fact]
    public void RoundTrip_DesktopConfigMessage()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopConfig,
            DesktopConfig = new DesktopConfig { Quality = 30, Scale = 0.25, TargetFps = 5 }
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopConfig, deserialized!.Type);
        Assert.NotNull(deserialized.DesktopConfig);
        Assert.Equal(30, deserialized.DesktopConfig!.Quality);
        Assert.Equal(0.25, deserialized.DesktopConfig.Scale);
        Assert.Equal(5, deserialized.DesktopConfig.TargetFps);
    }

    [Fact]
    public void DesktopConfig_DefaultValues_AreCorrect()
    {
        var config = new DesktopConfig();
        Assert.Equal(50, config.Quality);
        Assert.Equal(0.5, config.Scale);
        Assert.Equal(120, config.TargetFps);
    }

    [Fact]
    public void DesktopMeta_DefaultValues_AreCorrect()
    {
        var meta = new DesktopMeta { ScreenWidth = 1920, ScreenHeight = 1080 };
        Assert.Equal(1920, meta.ScreenWidth);
        Assert.Equal(1080, meta.ScreenHeight);
    }

    [Fact]
    public void InputEvent_DefaultValues_AreCorrect()
    {
        var evt = new InputEvent();
        Assert.Equal(string.Empty, evt.EventType);
        Assert.Null(evt.X);
        Assert.Null(evt.Y);
        Assert.Null(evt.Button);
        Assert.Null(evt.KeyCode);
        Assert.Null(evt.Text);
        Assert.Null(evt.DeltaX);
        Assert.Null(evt.DeltaY);
    }

    [Fact]
    public void RoundTrip_DesktopWindowQuery_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopWindowQuery,
            DesktopWindowQuery = new DesktopWindowQuery
            {
                RequestId = "query-1",
                SearchText = "code",
                Limit = 12,
                IncludeAllDesktops = false,
            },
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopWindowQuery, deserialized!.Type);
        Assert.NotNull(deserialized.DesktopWindowQuery);
        Assert.Equal("query-1", deserialized.DesktopWindowQuery!.RequestId);
        Assert.Equal("code", deserialized.DesktopWindowQuery.SearchText);
        Assert.Equal(12, deserialized.DesktopWindowQuery.Limit);
        Assert.False(deserialized.DesktopWindowQuery.IncludeAllDesktops);
    }

    [Fact]
    public void RoundTrip_DesktopWindowAction_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopWindowAction,
            DesktopWindowAction = new DesktopWindowAction
            {
                RequestId = "action-1",
                Action = DesktopWindowActionTypes.Resize,
                WindowId = "window-123",
                Width = 1600,
                Height = 900,
            },
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopWindowAction, deserialized!.Type);
        Assert.NotNull(deserialized.DesktopWindowAction);
        Assert.Equal("action-1", deserialized.DesktopWindowAction!.RequestId);
        Assert.Equal(DesktopWindowActionTypes.Resize, deserialized.DesktopWindowAction.Action);
        Assert.Equal("window-123", deserialized.DesktopWindowAction.WindowId);
        Assert.Equal(1600, deserialized.DesktopWindowAction.Width);
        Assert.Equal(900, deserialized.DesktopWindowAction.Height);
    }

    [Fact]
    public void RoundTrip_DesktopPointerBatch_FullSample_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopPointerBatch,
            DesktopPointerBatch = new Remex.Core.Models.DesktopPointerBatch
            {
                StreamMappingId = "stream-1",
                Samples =
                [
                    new Remex.Core.Models.DesktopPointerSample
                    {
                        ProtocolVersion = 1,
                        Timestamp = 123456789L,
                        PointerId = 1,
                        DeviceKind = Remex.Core.Models.PointerDeviceKind.Stylus,
                        ToolKind = Remex.Core.Models.PointerToolKind.Pen,
                        Phase = Remex.Core.Models.PointerPhase.ContactMove,
                        LogicalX = 800.5f,
                        LogicalY = 450.25f,
                        Dx = 2.0f,
                        Dy = -1.5f,
                        Pressure = 0.75f,
                        HoverDistance = null,
                        TiltX = 15.0f,
                        TiltY = -10.0f,
                        Orientation = 180.0f,
                        ButtonMask = 0,
                        StreamMappingId = "stream-1",
                        CoalescedHistory = null,
                    },
                ],
            },
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopPointerBatch, deserialized!.Type);
        Assert.NotNull(deserialized.DesktopPointerBatch);
        var batch = deserialized.DesktopPointerBatch!;
        Assert.Equal("stream-1", batch.StreamMappingId);
        Assert.Single(batch.Samples);
        var sample = batch.Samples[0];
        Assert.Equal(1, sample.ProtocolVersion);
        Assert.Equal(123456789L, sample.Timestamp);
        Assert.Equal(1, sample.PointerId);
        Assert.Equal(Remex.Core.Models.PointerDeviceKind.Stylus, sample.DeviceKind);
        Assert.Equal(Remex.Core.Models.PointerToolKind.Pen, sample.ToolKind);
        Assert.Equal(Remex.Core.Models.PointerPhase.ContactMove, sample.Phase);
        Assert.Equal(800.5f, sample.LogicalX);
        Assert.Equal(450.25f, sample.LogicalY);
        Assert.Equal(2.0f, sample.Dx);
        Assert.Equal(-1.5f, sample.Dy);
        Assert.Equal(0.75f, sample.Pressure);
        Assert.Null(sample.HoverDistance);
        Assert.Equal(15.0f, sample.TiltX);
        Assert.Equal(-10.0f, sample.TiltY);
        Assert.Equal(180.0f, sample.Orientation);
        Assert.Equal(0, sample.ButtonMask);
        Assert.Null(sample.CoalescedHistory);
    }

    [Fact]
    public void RoundTrip_DesktopPointerBatch_PhaseEnumWireValues()
    {
        // Verify each PointerPhase round-trips correctly via string enum conversion.
        var phases = new[]
        {
            Remex.Core.Models.PointerPhase.HoverStart,
            Remex.Core.Models.PointerPhase.HoverMove,
            Remex.Core.Models.PointerPhase.HoverEnd,
            Remex.Core.Models.PointerPhase.ContactStart,
            Remex.Core.Models.PointerPhase.ContactMove,
            Remex.Core.Models.PointerPhase.ContactEnd,
            Remex.Core.Models.PointerPhase.ButtonPress,
            Remex.Core.Models.PointerPhase.ButtonRelease,
        };

        foreach (var phase in phases)
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.DesktopPointerBatch,
                DesktopPointerBatch = new Remex.Core.Models.DesktopPointerBatch
                {
                    Samples =
                    [
                        new Remex.Core.Models.DesktopPointerSample
                        {
                            Phase = phase,
                            LogicalX = 0,
                            LogicalY = 0,
                        },
                    ],
                },
            };
            var deserialized = MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));
            Assert.NotNull(deserialized?.DesktopPointerBatch);
            Assert.Equal(phase, deserialized!.DesktopPointerBatch!.Samples[0].Phase);
        }
    }

    [Fact]
    public void RoundTrip_DesktopPointerBatch_ToolKindEnumWireValues()
    {
        // Verify each PointerToolKind round-trips correctly.
        var toolKinds = new[]
        {
            Remex.Core.Models.PointerToolKind.None,
            Remex.Core.Models.PointerToolKind.Pen,
            Remex.Core.Models.PointerToolKind.Eraser,
            Remex.Core.Models.PointerToolKind.Mouse,
            Remex.Core.Models.PointerToolKind.Finger,
        };

        foreach (var toolKind in toolKinds)
        {
            var msg = new RemexMessage
            {
                Type = MessageTypes.DesktopPointerBatch,
                DesktopPointerBatch = new Remex.Core.Models.DesktopPointerBatch
                {
                    Samples =
                    [
                        new Remex.Core.Models.DesktopPointerSample
                        {
                            ToolKind = toolKind,
                            LogicalX = 0,
                            LogicalY = 0,
                        },
                    ],
                },
            };
            var deserialized = MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));
            Assert.NotNull(deserialized?.DesktopPointerBatch);
            Assert.Equal(toolKind, deserialized!.DesktopPointerBatch!.Samples[0].ToolKind);
        }
    }

    [Fact]
    public void RoundTrip_DesktopPointerBatch_NullOptionalFields_RoundTripCorrectly()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopPointerBatch,
            DesktopPointerBatch = new Remex.Core.Models.DesktopPointerBatch
            {
                StreamMappingId = null,
                Samples =
                [
                    new Remex.Core.Models.DesktopPointerSample
                    {
                        LogicalX = 100,
                        LogicalY = 200,
                        HoverDistance = null,
                        TiltX = null,
                        TiltY = null,
                        Orientation = null,
                        CoalescedHistory = null,
                    },
                ],
            },
        };

        // Must deserialize correctly — null optional fields remain null after round-trip.
        var deserialized = MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));
        Assert.NotNull(deserialized?.DesktopPointerBatch);
        var sample = deserialized!.DesktopPointerBatch!.Samples[0];
        Assert.Equal(100f, sample.LogicalX);
        Assert.Equal(200f, sample.LogicalY);
        Assert.Null(sample.HoverDistance);
        Assert.Null(sample.TiltX);
        Assert.Null(sample.TiltY);
        Assert.Null(sample.Orientation);
        Assert.Null(sample.CoalescedHistory);
    }

    [Fact]
    public void RoundTrip_DesktopPointerBatch_CoalescedHistory_RoundTrips()
    {
        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopPointerBatch,
            DesktopPointerBatch = new Remex.Core.Models.DesktopPointerBatch
            {
                Samples =
                [
                    new Remex.Core.Models.DesktopPointerSample
                    {
                        Timestamp = 1000,
                        LogicalX = 300,
                        LogicalY = 400,
                        Phase = Remex.Core.Models.PointerPhase.ContactMove,
                        CoalescedHistory =
                        [
                            new Remex.Core.Models.DesktopPointerSample
                            {
                                Timestamp = 990,
                                LogicalX = 290,
                                LogicalY = 395,
                                Phase = Remex.Core.Models.PointerPhase.ContactMove,
                            },
                            new Remex.Core.Models.DesktopPointerSample
                            {
                                Timestamp = 995,
                                LogicalX = 295,
                                LogicalY = 397,
                                Phase = Remex.Core.Models.PointerPhase.ContactMove,
                            },
                        ],
                    },
                ],
            },
        };

        var deserialized = MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));
        Assert.NotNull(deserialized?.DesktopPointerBatch);
        var primary = deserialized!.DesktopPointerBatch!.Samples[0];
        Assert.NotNull(primary.CoalescedHistory);
        Assert.Equal(2, primary.CoalescedHistory!.Count);
        Assert.Equal(990L, primary.CoalescedHistory[0].Timestamp);
        Assert.Equal(290f, primary.CoalescedHistory[0].LogicalX);
        Assert.Equal(995L, primary.CoalescedHistory[1].Timestamp);
    }

    [Fact]
    public void RoundTrip_DesktopPointerBatch_MultipleSamples_PreservesOrder()
    {
        var samples = new[]
        {
            new Remex.Core.Models.DesktopPointerSample { Timestamp = 1, LogicalX = 10, LogicalY = 20, Phase = Remex.Core.Models.PointerPhase.ContactStart },
            new Remex.Core.Models.DesktopPointerSample { Timestamp = 2, LogicalX = 15, LogicalY = 25, Phase = Remex.Core.Models.PointerPhase.ContactMove },
            new Remex.Core.Models.DesktopPointerSample { Timestamp = 3, LogicalX = 20, LogicalY = 30, Phase = Remex.Core.Models.PointerPhase.ContactEnd },
        };

        var msg = new RemexMessage
        {
            Type = MessageTypes.DesktopPointerBatch,
            DesktopPointerBatch = new Remex.Core.Models.DesktopPointerBatch
            {
                Samples = new System.Collections.Generic.List<Remex.Core.Models.DesktopPointerSample>(samples),
            },
        };

        var deserialized = MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));
        Assert.NotNull(deserialized?.DesktopPointerBatch);
        var result = deserialized!.DesktopPointerBatch!.Samples;
        Assert.Equal(3, result.Count);
        Assert.Equal(1L, result[0].Timestamp);
        Assert.Equal(Remex.Core.Models.PointerPhase.ContactStart, result[0].Phase);
        Assert.Equal(2L, result[1].Timestamp);
        Assert.Equal(Remex.Core.Models.PointerPhase.ContactMove, result[1].Phase);
        Assert.Equal(3L, result[2].Timestamp);
        Assert.Equal(Remex.Core.Models.PointerPhase.ContactEnd, result[2].Phase);
    }

    [Fact]
    public void RoundTrip_DesktopWindowResult_PreservesAllFields()
    {
        var original = new RemexMessage
        {
            Type = MessageTypes.DesktopWindowResult,
            DesktopWindowResult = new DesktopWindowResult
            {
                RequestId = "result-1",
                Action = DesktopWindowActionTypes.Activate,
                Success = true,
                Backend = "kdotool",
                CurrentDesktop = 2,
                DesktopCount = 4,
                Windows =
                [
                    new DesktopWindowInfo
                    {
                        Id = "window-1",
                        Title = "Code",
                        ClassName = "code",
                        DesktopNumber = 2,
                        Width = 1280,
                        Height = 720,
                        IsActive = true,
                    },
                ],
            },
        };

        var bytes = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize(bytes);

        Assert.NotNull(deserialized);
        Assert.Equal(MessageTypes.DesktopWindowResult, deserialized!.Type);
        Assert.NotNull(deserialized.DesktopWindowResult);
        Assert.True(deserialized.DesktopWindowResult!.Success);
        Assert.Equal("kdotool", deserialized.DesktopWindowResult.Backend);
        Assert.Equal(2, deserialized.DesktopWindowResult.CurrentDesktop);
        Assert.Equal(4, deserialized.DesktopWindowResult.DesktopCount);
        Assert.Single(deserialized.DesktopWindowResult.Windows!);
        Assert.Equal("window-1", deserialized.DesktopWindowResult.Windows![0].Id);
        Assert.Equal("Code", deserialized.DesktopWindowResult.Windows[0].Title);
    }
}
