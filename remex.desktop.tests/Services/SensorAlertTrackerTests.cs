using System;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Behaviour coverage for <see cref="SensorAlertTracker"/>: idempotent trips, acknowledge and
/// acknowledge-all, the tripped count, <see cref="SensorAlertTracker.TrippedChanged"/>, and the
/// 60-second notification cooldown against a fake clock — including that acknowledging a sensor
/// does not reset its cooldown.
/// </summary>
public class SensorAlertTrackerTests
{
    private static SensorAlert MakeAlert(string name = "CPU Package", double threshold = 90) => new()
    {
        SensorName = name,
        Threshold = threshold,
        Direction = AlertDirection.Above,
        Severity = AlertSeverity.Critical,
    };

    [Fact]
    public void Trip_RecordsSensorAsTripped()
    {
        var tracker = new SensorAlertTracker();
        var now = DateTimeOffset.UtcNow;

        tracker.Trip("CPU Package", 91.2, MakeAlert(), now);

        tracker.IsTripped("CPU Package").Should().BeTrue();
        tracker.TrippedCount.Should().Be(1);
    }

    [Fact]
    public void Trip_FirstCall_ReturnsTrue()
    {
        var tracker = new SensorAlertTracker();

        var notify = tracker.Trip("CPU Package", 91.2, MakeAlert(), DateTimeOffset.UtcNow);

        notify.Should().BeTrue();
    }

    [Fact]
    public void Trip_IsIdempotent_UpdatesAtAndValueOnRepeatCrossing()
    {
        var tracker = new SensorAlertTracker();
        var alert = MakeAlert();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var second = first.AddSeconds(5);

        tracker.Trip("CPU Package", 91.0, alert, first);
        tracker.Trip("CPU Package", 93.5, alert, second);

        tracker.TrippedCount.Should().Be(1);
        var entry = tracker.Tripped.Should().ContainSingle().Subject;
        entry.At.Should().Be(second);
        entry.Value.Should().Be(93.5);
    }

    [Fact]
    public void Trip_WithinCooldown_ReturnsFalseSecondTime()
    {
        var tracker = new SensorAlertTracker();
        var alert = MakeAlert();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Trip("CPU Package", 91.0, alert, first);
        var secondNotify = tracker.Trip("CPU Package", 92.0, alert, first.AddSeconds(30));

        secondNotify.Should().BeFalse();
    }

    [Fact]
    public void Trip_AfterCooldownElapses_ReturnsTrueAgain()
    {
        var tracker = new SensorAlertTracker();
        var alert = MakeAlert();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Trip("CPU Package", 91.0, alert, first);
        tracker.Trip("CPU Package", 92.0, alert, first.AddSeconds(30));
        var thirdNotify = tracker.Trip("CPU Package", 93.0, alert, first.AddSeconds(61));

        thirdNotify.Should().BeTrue();
    }

    [Fact]
    public void Trip_RecordsRegardlessOfNotificationReturnValue()
    {
        var tracker = new SensorAlertTracker();
        var alert = MakeAlert();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Trip("CPU Package", 91.0, alert, first);
        tracker.Trip("CPU Package", 92.0, alert, first.AddSeconds(1));

        tracker.IsTripped("CPU Package").Should().BeTrue();
        tracker.Tripped.Should().ContainSingle().Which.Value.Should().Be(92.0);
    }

    [Fact]
    public void Trip_ParameterlessOverload_UsesInjectedClock()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tracker = new SensorAlertTracker(() => now);

        tracker.Trip("CPU Package", 91.0, MakeAlert());

        tracker.Tripped.Should().ContainSingle().Which.At.Should().Be(now);
    }

    [Fact]
    public void Acknowledge_RemovesTrippedEntry()
    {
        var tracker = new SensorAlertTracker();
        tracker.Trip("CPU Package", 91.0, MakeAlert(), DateTimeOffset.UtcNow);

        tracker.Acknowledge("CPU Package");

        tracker.IsTripped("CPU Package").Should().BeFalse();
        tracker.TrippedCount.Should().Be(0);
    }

    [Fact]
    public void Acknowledge_DoesNotResetCooldown()
    {
        var tracker = new SensorAlertTracker();
        var alert = MakeAlert();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Trip("CPU Package", 91.0, alert, first);
        tracker.Acknowledge("CPU Package");
        var notify = tracker.Trip("CPU Package", 92.0, alert, first.AddSeconds(30));

        notify.Should().BeFalse();
    }

    [Fact]
    public void AcknowledgeAll_ClearsEverySensor()
    {
        var tracker = new SensorAlertTracker();
        tracker.Trip("CPU Package", 91.0, MakeAlert("CPU Package"), DateTimeOffset.UtcNow);
        tracker.Trip("GPU Core", 88.0, MakeAlert("GPU Core"), DateTimeOffset.UtcNow);

        tracker.AcknowledgeAll();

        tracker.TrippedCount.Should().Be(0);
        tracker.Tripped.Should().BeEmpty();
    }

    [Fact]
    public void TrippedChanged_FiresOnTripAcknowledgeAndAcknowledgeAll()
    {
        var tracker = new SensorAlertTracker();
        var fireCount = 0;
        tracker.TrippedChanged += () => fireCount++;

        tracker.Trip("CPU Package", 91.0, MakeAlert(), DateTimeOffset.UtcNow);
        tracker.Acknowledge("CPU Package");
        tracker.Trip("CPU Package", 91.0, MakeAlert(), DateTimeOffset.UtcNow);
        tracker.AcknowledgeAll();

        fireCount.Should().Be(4);
    }

    [Fact]
    public void IsTripped_IsCaseInsensitive()
    {
        var tracker = new SensorAlertTracker();
        tracker.Trip("CPU Package", 91.0, MakeAlert(), DateTimeOffset.UtcNow);

        tracker.IsTripped("cpu package").Should().BeTrue();
    }

    [Fact]
    public void Forget_ClearsCooldown_NextTripReturnsTrueImmediately()
    {
        var tracker = new SensorAlertTracker();
        var alert = MakeAlert();
        var first = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        tracker.Trip("CPU Package", 91.0, alert, first);
        tracker.Forget("CPU Package");
        var notify = tracker.Trip("CPU Package", 92.0, alert, first.AddSeconds(1));

        notify.Should().BeTrue();
    }

    [Fact]
    public void Forget_OnTrippedName_FiresTrippedChangedOnce()
    {
        var tracker = new SensorAlertTracker();
        tracker.Trip("CPU Package", 91.0, MakeAlert(), DateTimeOffset.UtcNow);
        var fireCount = 0;
        tracker.TrippedChanged += () => fireCount++;

        tracker.Forget("CPU Package");

        fireCount.Should().Be(1);
    }

    [Fact]
    public void Forget_OnUnknownName_FiresNothing()
    {
        var tracker = new SensorAlertTracker();
        var fireCount = 0;
        tracker.TrippedChanged += () => fireCount++;

        tracker.Forget("Unknown Sensor");

        fireCount.Should().Be(0);
    }
}
