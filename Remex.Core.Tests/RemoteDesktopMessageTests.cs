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
        Assert.Equal(10, config.TargetFps);
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
