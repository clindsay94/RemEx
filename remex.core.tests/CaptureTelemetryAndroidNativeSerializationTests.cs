using System.Text;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Native;
using Remex.Core.Serialization;

namespace Remex.Core.Tests;

/// <summary>
/// Round-trip coverage for the capture/telemetry and Android-native wire types that had zero mentions
/// anywhere in this suite. A missing or wrong <c>[JsonSerializable]</c> registration in
/// <see cref="RemexJsonSerializerContext"/> is a runtime-only NativeAOT failure — it compiles clean and
/// only breaks on device — so every registered type needs a genuine round trip through the real
/// source-generated serializer, not just "did not throw".
/// </summary>
public class CaptureTelemetryAndroidNativeSerializationTests
{
    private static RemexMessage RoundTripMessage(RemexMessage message)
    {
        var bytes = MessageSerializer.Serialize(message);
        var back = MessageSerializer.Deserialize(bytes);
        Assert.NotNull(back);
        return back!;
    }

    // ── Types reachable through the RemexMessage envelope: exercised through the real MessageSerializer ──

    [Fact]
    public void RoundTrip_DesktopTargetSwitchRequest_PreservesTargetAndVersion()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.DesktopTargetSwitch,
            DesktopTargetSwitch = new DesktopTargetSwitchRequest
            {
                Target = new DesktopCaptureTarget { CaptureMode = DesktopCaptureMode.Monitor, DisplayId = "disp-2" },
                DisplayListVersion = 7,
            },
        });

        var t = back.DesktopTargetSwitch;
        Assert.NotNull(t);
        Assert.Equal(DesktopCaptureMode.Monitor, t!.Target.CaptureMode);
        Assert.Equal("disp-2", t.Target.DisplayId);
        Assert.Equal(7, t.DisplayListVersion);
    }

    [Fact]
    public void RoundTrip_DesktopTargetSwitchRequest_DefaultsOmitDisplayListVersion()
    {
        var message = new RemexMessage
        {
            Type = MessageTypes.DesktopTargetSwitch,
            DesktopTargetSwitch = new DesktopTargetSwitchRequest(),
        };
        var back = RoundTripMessage(message);
        Assert.Equal(DesktopCaptureMode.VirtualDesktop, back.DesktopTargetSwitch!.Target.CaptureMode);
        Assert.Null(back.DesktopTargetSwitch.DisplayListVersion);

        var json = Encoding.UTF8.GetString(MessageSerializer.Serialize(message));
        Assert.DoesNotContain("displayListVersion", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_DesktopCursorShape_PreservesBitmapAndHotspot()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.DesktopCursorShape,
            DesktopCursorShape = new DesktopCursorShape
            {
                ShapeSerial = 42,
                Width = 16,
                Height = 24,
                HotspotX = 3,
                HotspotY = 5,
                PixelFormat = "bgra8888",
                ShapeBytes = [1, 2, 3, 4, 255],
            },
        });

        var s = back.DesktopCursorShape;
        Assert.NotNull(s);
        Assert.Equal(42, s!.ShapeSerial);
        Assert.Equal(16, s.Width);
        Assert.Equal(24, s.Height);
        Assert.Equal(3, s.HotspotX);
        Assert.Equal(5, s.HotspotY);
        Assert.Equal("bgra8888", s.PixelFormat);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 255 }, s.ShapeBytes);
    }

    [Fact]
    public void RoundTrip_DesktopStreamDescriptor_PreservesAllFields()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.DesktopStreamDescriptor,
            DesktopStreamDescriptor = new DesktopStreamDescriptor
            {
                StreamMappingId = "map-1",
                StreamSerial = 9,
                LogicalWidth = 1920,
                LogicalHeight = 1080,
                PixelWidth = 3840,
                PixelHeight = 2160,
                EncodedWidth = 3838,
                EncodedHeight = 2158,
            },
        });

        var d = back.DesktopStreamDescriptor;
        Assert.NotNull(d);
        Assert.Equal("map-1", d!.StreamMappingId);
        Assert.Equal(9, d.StreamSerial);
        Assert.Equal(1920, d.LogicalWidth);
        Assert.Equal(1080, d.LogicalHeight);
        Assert.Equal(3840, d.PixelWidth);
        Assert.Equal(2160, d.PixelHeight);
        Assert.Equal(3838, d.EncodedWidth);
        Assert.Equal(2158, d.EncodedHeight);
    }

    [Fact]
    public void RoundTrip_ReconnectProof_PreservesHmacAndClientId()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.ReconnectProof,
            ReconnectProof = new ReconnectProof { ProofHmacBase64 = "aGFzaA==", ClientId = "client-9" },
        });

        Assert.Equal("aGFzaA==", back.ReconnectProof!.ProofHmacBase64);
        Assert.Equal("client-9", back.ReconnectProof.ClientId);
    }

    [Fact]
    public void RoundTrip_DesktopCursorState_PreservesAllSerialsAndPosition()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.DesktopCursorState,
            DesktopCursorState = new DesktopCursorState
            {
                CursorSerial = 100,
                StreamSerial = 5,
                X = 640,
                Y = 480,
                Visible = false,
                ShapeSerial = 12,
                HotspotX = 2,
                HotspotY = 2,
            },
        });

        var s = back.DesktopCursorState;
        Assert.NotNull(s);
        Assert.Equal(100, s!.CursorSerial);
        Assert.Equal(5, s.StreamSerial);
        Assert.Equal(640, s.X);
        Assert.Equal(480, s.Y);
        Assert.False(s.Visible);
        Assert.Equal(12, s.ShapeSerial);
        Assert.Equal(2, s.HotspotX);
        Assert.Equal(2, s.HotspotY);
    }

    [Fact]
    public void RoundTrip_DesktopCodecInfo_PreservesCodecProfileAndBitrate_ViaDesktopMeta()
    {
        // DesktopCodecInfo has no envelope slot of its own — it only ever travels nested in
        // DesktopMeta.CodecInfo, so the real wire path is through that slot.
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.DesktopMeta,
            DesktopMeta = new DesktopMeta
            {
                CodecInfo = new DesktopCodecInfo
                {
                    Codec = DesktopCodecKind.H264,
                    Profile = DesktopH264Profile.High,
                    EncoderBackend = DesktopEncoderBackend.Nvenc,
                    TargetFps = 60,
                    TargetBitrateKbps = 8000,
                },
            },
        });

        var codec = back.DesktopMeta?.CodecInfo;
        Assert.NotNull(codec);
        Assert.Equal(DesktopCodecKind.H264, codec!.Codec);
        Assert.Equal(DesktopH264Profile.High, codec.Profile);
        Assert.Equal(DesktopEncoderBackend.Nvenc, codec.EncoderBackend);
        Assert.Equal(60, codec.TargetFps);
        Assert.Equal(8000, codec.TargetBitrateKbps);
    }

    [Fact]
    public void RoundTrip_DesktopCodecInfo_MjpegLeavesProfileAndBitrateNull_ViaDesktopMeta()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.DesktopMeta,
            DesktopMeta = new DesktopMeta { CodecInfo = new DesktopCodecInfo { Codec = DesktopCodecKind.Mjpeg, TargetFps = 15 } },
        });

        var codec = back.DesktopMeta!.CodecInfo!;
        Assert.Equal(DesktopCodecKind.Mjpeg, codec.Codec);
        Assert.Null(codec.Profile);
        Assert.Null(codec.EncoderBackend);
        Assert.Null(codec.TargetBitrateKbps);
    }

    [Fact]
    public void RoundTrip_ClipboardPushResult_PreservesReason()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.ClipboardPushResult,
            ClipboardPushResult = new ClipboardPushResult { Reason = "refused" },
        });

        Assert.Equal("refused", back.ClipboardPushResult!.Reason);
    }

    [Fact]
    public void RoundTrip_DesktopDisplayCatalog_PreservesModesAndDisplays()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.DesktopDisplayList,
            DesktopDisplayCatalog = new DesktopDisplayCatalog
            {
                DisplayListVersion = 3,
                SupportedCaptureModes = [DesktopCaptureMode.VirtualDesktop, DesktopCaptureMode.Monitor],
                Displays =
                [
                    new DesktopDisplayInfo
                    {
                        DisplayId = "d0",
                        PersistentDisplayKey = "key-0",
                        Name = "Primary",
                        IsPrimary = true,
                        Left = 0,
                        Top = 0,
                        Width = 1920,
                        Height = 1080,
                    },
                ],
            },
        });

        var c = back.DesktopDisplayCatalog;
        Assert.NotNull(c);
        Assert.Equal(3, c!.DisplayListVersion);
        Assert.Equal(2, c.SupportedCaptureModes.Count);
        Assert.Contains(DesktopCaptureMode.Monitor, c.SupportedCaptureModes);
        Assert.Single(c.Displays);
        Assert.True(c.Displays[0].IsPrimary);
        Assert.Equal("key-0", c.Displays[0].PersistentDisplayKey);
    }

    [Fact]
    public void RoundTrip_TelemetryPayload_PreservesSensorsAndMetricKind()
    {
        var back = RoundTripMessage(new RemexMessage
        {
            Type = MessageTypes.Telemetry,
            Telemetry = new TelemetryPayload
            {
                UptimeText = "3d 4h",
                Sensors =
                [
                    new SensorReading
                    {
                        Name = "CPU Package",
                        Value = 42.5,
                        Unit = "°C",
                        Category = "Temperature",
                        Source = "HWInfo",
                        Kind = MetricKind.CpuTempC,
                        Id = "hwi:1:2",
                        Group = "CPU",
                    },
                ],
            },
        });

        var t = back.Telemetry;
        Assert.NotNull(t);
        Assert.Equal("3d 4h", t!.UptimeText);
        Assert.Single(t.Sensors);
        var s = t.Sensors[0];
        Assert.Equal("CPU Package", s.Name);
        Assert.Equal(42.5, s.Value);
        Assert.Equal("°C", s.Unit);
        Assert.Equal(MetricKind.CpuTempC, s.Kind);
        Assert.Equal("hwi:1:2", s.Id);
        Assert.Equal("CPU", s.Group);
    }

    [Theory]
    [InlineData(MetricKind.Unknown, "Unknown")]
    [InlineData(MetricKind.CpuLoad, "CpuLoad")]
    [InlineData(MetricKind.RamUsedGb, "RamUsedGb")]
    [InlineData(MetricKind.DiskRateMBs, "DiskRateMBs")]
    public void MetricKind_SerializesToItsMemberNameVerbatim(MetricKind kind, string expectedToken)
    {
        // The wire contract is the enum's member name with no naming policy applied (see MetricKind.cs
        // remarks) — a drift here silently breaks the Kotlin/Avalonia clients that parse it by hand.
        var message = new RemexMessage
        {
            Type = MessageTypes.Telemetry,
            Telemetry = new TelemetryPayload { Sensors = [new SensorReading { Kind = kind }] },
        };
        var json = Encoding.UTF8.GetString(MessageSerializer.Serialize(message));
        Assert.Contains($"\"kind\":\"{expectedToken}\"", json, StringComparison.Ordinal);

        var back = RoundTripMessage(message);
        Assert.Equal(kind, back.Telemetry!.Sensors[0].Kind);
    }

    // ── Types with no RemexMessage envelope slot: round-tripped directly through the real source-generated context ──

    private static T RoundTripDirect<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        var json = RemexJson.Serialize(value, typeInfo);
        var back = RemexJson.Deserialize(json, typeInfo);
        Assert.NotNull(back);
        return back!;
    }

    [Fact]
    public void RoundTrip_MonitorInfo_PreservesGeometryAndPrimaryFlag()
    {
        var back = RoundTripDirect(
            new MonitorInfo { Index = 1, Name = "DP-1", Left = -1920, Top = 0, Width = 1920, Height = 1080, IsPrimary = false },
            RemexJsonSerializerContext.Default.MonitorInfo);

        Assert.Equal(1, back.Index);
        Assert.Equal("DP-1", back.Name);
        Assert.Equal(-1920, back.Left);
        Assert.Equal(0, back.Top);
        Assert.Equal(1920, back.Width);
        Assert.Equal(1080, back.Height);
        Assert.False(back.IsPrimary);
    }

    [Fact]
    public void RoundTrip_DesktopFrameEnvelopeHeader_PreservesStreamSequenceCodecAndFlags()
    {
        var back = RoundTripDirect(
            new DesktopFrameEnvelopeHeader(
                StreamSerial: 77,
                Sequence: 12345,
                Codec: DesktopCodecKind.H264,
                Flags: DesktopFrameFlags.KeyFrame),
            RemexJsonSerializerContext.Default.DesktopFrameEnvelopeHeader);

        Assert.Equal(77, back.StreamSerial);
        Assert.Equal(12345, back.Sequence);
        Assert.Equal(DesktopCodecKind.H264, back.Codec);
        Assert.Equal(DesktopFrameFlags.KeyFrame, back.Flags);
    }

    [Fact]
    public void RoundTrip_AndroidNativeInitRequest_PreservesConnectionSettings()
    {
        var back = RoundTripDirect(
            new AndroidNativeInitRequest
            {
                Host = "192.168.1.50",
                Port = 5005,
                SpkiHash = "c3BraQ==",
                ClientId = "client-a",
                ReconnectSecret = "c2VjcmV0",
                TelemetryPollIntervalMs = 2000,
                StartTelemetryPolling = false,
                WarmupTelemetry = false,
            },
            RemexJsonSerializerContext.Default.AndroidNativeInitRequest);

        Assert.Equal("192.168.1.50", back.Host);
        Assert.Equal(5005, back.Port);
        Assert.Equal("c3BraQ==", back.SpkiHash);
        Assert.Equal("client-a", back.ClientId);
        Assert.Equal("c2VjcmV0", back.ReconnectSecret);
        Assert.Equal(2000, back.TelemetryPollIntervalMs);
        Assert.False(back.StartTelemetryPolling);
        Assert.False(back.WarmupTelemetry);
    }

    [Fact]
    public void RoundTrip_AndroidNativeOperationResponse_PreservesSuccessMessageAndError()
    {
        var back = RoundTripDirect(
            new AndroidNativeOperationResponse { Success = false, Message = "failed", Error = "boom" },
            RemexJsonSerializerContext.Default.AndroidNativeOperationResponse);

        Assert.False(back.Success);
        Assert.Equal("failed", back.Message);
        Assert.Equal("boom", back.Error);
    }

    [Fact]
    public void RoundTrip_AndroidNativeInitializationResponse_PreservesCapabilityFlags()
    {
        var back = RoundTripDirect(
            new AndroidNativeInitializationResponse
            {
                Success = true,
                Message = "ok",
                TelemetryAvailable = true,
                BackgroundLoopStarted = true,
                IpcAvailable = false,
                WakeOnLanAvailable = true,
                TelemetryPollIntervalMs = 1500,
            },
            RemexJsonSerializerContext.Default.AndroidNativeInitializationResponse);

        Assert.True(back.Success);
        Assert.True(back.TelemetryAvailable);
        Assert.True(back.BackgroundLoopStarted);
        Assert.False(back.IpcAvailable);
        Assert.True(back.WakeOnLanAvailable);
        Assert.Equal(1500, back.TelemetryPollIntervalMs);
    }

    [Fact]
    public void RoundTrip_AndroidNativeTelemetryResponse_PreservesNestedTelemetryPayload()
    {
        var back = RoundTripDirect(
            new AndroidNativeTelemetryResponse
            {
                Success = true,
                Message = "ok",
                Telemetry = new TelemetryPayload { UptimeText = "1h", Sensors = [new SensorReading { Name = "RAM", Value = 16, Kind = MetricKind.RamUsedGb }] },
            },
            RemexJsonSerializerContext.Default.AndroidNativeTelemetryResponse);

        Assert.True(back.Success);
        Assert.NotNull(back.Telemetry);
        Assert.Equal("1h", back.Telemetry!.UptimeText);
        Assert.Single(back.Telemetry.Sensors);
        Assert.Equal(MetricKind.RamUsedGb, back.Telemetry.Sensors[0].Kind);
    }

    [Fact]
    public void RoundTrip_AndroidNativeTelemetryResponse_FailureLeavesTelemetryNull()
    {
        var back = RoundTripDirect(
            new AndroidNativeTelemetryResponse { Success = false, Message = "unavailable", Error = "no sensors" },
            RemexJsonSerializerContext.Default.AndroidNativeTelemetryResponse);

        Assert.False(back.Success);
        Assert.Null(back.Telemetry);
        Assert.Equal("no sensors", back.Error);
    }
}
