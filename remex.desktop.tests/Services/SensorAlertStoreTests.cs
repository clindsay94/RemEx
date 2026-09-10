using System.Linq;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Behaviour coverage for <see cref="SensorAlertStore"/>: upsert-by-name, case-insensitive
/// lookup, removal, clearing, bulk replace, and the "<see cref="SensorAlertStore.Changed"/>
/// fires unconditionally" contract callers rely on to re-apply alerts to sensors.
/// </summary>
public class SensorAlertStoreTests
{
    private static SensorAlert MakeAlert(string name, double threshold = 90) => new()
    {
        SensorName = name,
        Threshold = threshold,
        Direction = AlertDirection.Above,
        Severity = AlertSeverity.Critical,
    };

    [Fact]
    public void Set_AddsNewAlert()
    {
        var store = new SensorAlertStore();
        var alert = MakeAlert("CPU Package");

        store.Set(alert);

        store.All.Should().ContainSingle().Which.Should().Be(alert);
    }

    [Fact]
    public void Set_UpsertsByName()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("CPU Package", 90));

        store.Set(MakeAlert("CPU Package", 95));

        store.All.Should().ContainSingle().Which.Threshold.Should().Be(95);
    }

    [Fact]
    public void Set_IsCaseInsensitiveByName()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("CPU Package", 90));

        store.Set(MakeAlert("cpu package", 95));

        store.All.Should().ContainSingle().Which.Threshold.Should().Be(95);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("CPU Package"));

        store.TryGet("cpu package", out var alert).Should().BeTrue();
        alert!.SensorName.Should().Be("CPU Package");
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenNotConfigured()
    {
        var store = new SensorAlertStore();

        store.TryGet("Missing", out _).Should().BeFalse();
    }

    [Fact]
    public void Remove_DeletesByCaseInsensitiveName()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("CPU Package"));

        store.Remove("cpu package");

        store.All.Should().BeEmpty();
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("CPU Package"));
        store.Set(MakeAlert("GPU Core"));

        store.Clear();

        store.All.Should().BeEmpty();
    }

    [Fact]
    public void ReplaceAll_ReplacesEntireSet()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("Stale Sensor"));

        store.ReplaceAll(new[] { MakeAlert("CPU Package"), MakeAlert("GPU Core") });

        store.All.Select(a => a.SensorName).Should().BeEquivalentTo("CPU Package", "GPU Core");
    }

    [Fact]
    public void Changed_FiresOncePerSet()
    {
        var store = new SensorAlertStore();
        var fireCount = 0;
        store.Changed += () => fireCount++;

        store.Set(MakeAlert("CPU Package"));

        fireCount.Should().Be(1);
    }

    [Fact]
    public void Changed_FiresOnIdenticalSet()
    {
        var store = new SensorAlertStore();
        var alert = MakeAlert("CPU Package");
        store.Set(alert);
        var fireCount = 0;
        store.Changed += () => fireCount++;

        store.Set(alert with { });

        fireCount.Should().Be(1);
    }

    [Fact]
    public void Changed_FiresOnceOnRemove()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("CPU Package"));
        var fireCount = 0;
        store.Changed += () => fireCount++;

        store.Remove("CPU Package");

        fireCount.Should().Be(1);
    }

    [Fact]
    public void Changed_FiresOnceOnClear()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("CPU Package"));
        var fireCount = 0;
        store.Changed += () => fireCount++;

        store.Clear();

        fireCount.Should().Be(1);
    }

    [Fact]
    public void Changed_FiresExactlyOnceForReplaceAll()
    {
        var store = new SensorAlertStore();
        var fireCount = 0;
        store.Changed += () => fireCount++;

        store.ReplaceAll(new[] { MakeAlert("CPU Package"), MakeAlert("GPU Core"), MakeAlert("RAM") });

        fireCount.Should().Be(1);
        store.All.Should().HaveCount(3);
    }
}
