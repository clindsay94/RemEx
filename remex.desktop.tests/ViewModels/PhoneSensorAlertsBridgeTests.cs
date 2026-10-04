using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Core.Services.Alerts;
using Remex.Core.Validation;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The desktop end of the PC's sensor alerts on the phone (RemEx-pp4cm.12): the tracker's own firing
/// (and nothing else) is sent to phones, a phone's edit lands in the PC's one <see cref="SensorAlertStore"/>
/// and is saved and shown on the PC, a refused edit is answered with the unchanged rules, and a rule
/// changed on the PC reaches the phones once.
/// </summary>
/// <remarks>
/// The canvas is built the way <c>CanvasAlertStateTests</c> builds it, with the store's
/// <c>OnAlertStoreChanged</c> wired by hand: <c>InitializeAsync</c> awaits the dispatcher, which this
/// assembly cannot pump. The host's <see cref="IPhoneSensorAlerts"/> is a recording double (this
/// assembly cannot reference <c>Remex.Agent</c>); the real one is pinned by
/// <c>PhoneSensorAlertsTests</c> in <c>remex.agent.tests</c>.
/// </remarks>
public sealed class PhoneSensorAlertsBridgeTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("remex-phonealerts-").FullName;
    private readonly ThemeService _theme = new() { PostToUiThread = action => action() };
    private readonly FakeAlerts _alerts = new();
    private readonly SensorAlertStore _store = new();
    private readonly SensorAlertTracker _tracker = new();
    private DashboardLayoutService _layoutService = null!;
    private CanvasDashboardViewModel _canvas = null!;
    private PhoneSensorAlertsBridge? _bridge;

    public async Task InitializeAsync()
    {
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        await _layoutService.LoadAsync();

        var connection = new ConnectionViewModel { IsConnected = true };
        _canvas = new CanvasDashboardViewModel(connection, _layoutService, null!, _store, _tracker);
        var onAlertStoreChanged = typeof(CanvasDashboardViewModel)
            .GetMethod("OnAlertStoreChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _store.Changed += (Action)Delegate.CreateDelegate(typeof(Action), _canvas, onAlertStoreChanged);
    }

    public Task DisposeAsync()
    {
        _bridge?.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private PhoneSensorAlertsBridge Bridge() =>
        _bridge = new PhoneSensorAlertsBridge(_alerts, _store, _canvas, post: run => run());

    private static TelemetryPayload Reading(string name, double value) => new()
    {
        Sensors = [new SensorReading { Id = name, Name = name, Value = value, Unit = "°C" }],
    };

    /// <summary>A sensor the PC has seen and is reporting.</summary>
    private void Know(string name, double value = 40) => _canvas.ApplyTelemetry(Reading(name, value));

    private static SensorAlertChange Change(
        string name, double threshold = 90, AlertSeverity severity = AlertSeverity.Critical,
        AlertDirection direction = AlertDirection.Above) =>
        new() { SensorName = name, Threshold = threshold, Direction = direction, Severity = severity };

    private void PhoneSets(SensorAlertChange change) => _alerts.Raise(
        new PhoneSensorAlertRequest(PhoneSensorAlertRequestKind.Set, "phone-1", Change: change));

    private void PhoneRemoves(string name) => _alerts.Raise(
        new PhoneSensorAlertRequest(PhoneSensorAlertRequestKind.Remove, "phone-1", SensorName: name));

    // ── The PC's alert reaches the phone ─────────────────────────────────────

    [Fact]
    public void AFiringIsSentToPhonesWithTheDisplayNameUnitAndRule()
    {
        Know("cpu-pkg", 40);
        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert
        {
            SensorName = "cpu-pkg", Threshold = 90, Direction = AlertDirection.Above, Severity = AlertSeverity.Critical,
        });
        Bridge();

        _canvas.ApplyTelemetry(Reading("cpu-pkg", 95));

        var fired = _alerts.Fired.Should().ContainSingle().Subject;
        fired.SensorName.Should().Be("cpu-pkg");
        fired.Value.Should().Be(95);
        fired.Unit.Should().Be("°C");
        fired.Threshold.Should().Be(90);
        fired.Direction.Should().Be(AlertDirection.Above);
        fired.Severity.Should().Be(AlertSeverity.Critical);
        fired.DisplayName.Should().NotBeNullOrWhiteSpace();
        fired.FiredAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ThePcsSixtySecondCooldownAppliesToPhonesAndThereIsNoSecondEvaluator()
    {
        Know("cpu-pkg", 40);
        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert { SensorName = "cpu-pkg", Threshold = 90, Direction = AlertDirection.Above });
        Bridge();
        var pcToasts = new List<double>();
        _canvas.SensorAlertFired += (_, value) => pcToasts.Add(value);

        // Over, well back under (re-arms the sensor), over again, inside the cooldown.
        _canvas.ApplyTelemetry(Reading("cpu-pkg", 95));
        _canvas.ApplyTelemetry(Reading("cpu-pkg", 40));
        _canvas.ApplyTelemetry(Reading("cpu-pkg", 96));

        pcToasts.Should().ContainSingle("the tracker suppresses the second notification for 60 seconds");
        _alerts.Fired.Should().ContainSingle("phones get exactly what the PC's own notification gets");
    }

    [Fact]
    public void ReadingsThatDoNotCrossARuleSendNothing()
    {
        Know("cpu-pkg", 40);
        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert { SensorName = "cpu-pkg", Threshold = 90, Direction = AlertDirection.Above });
        Bridge();

        _canvas.ApplyTelemetry(Reading("cpu-pkg", 60));
        _canvas.ApplyTelemetry(Reading("other", 99));

        _alerts.Fired.Should().BeEmpty();
    }

    // ── A phone's edit is applied through the PC's store ─────────────────────

    [Fact]
    public void APhoneSetBecomesTheSensorsRuleAndIsPublishedWithUnitAndLiveValue()
    {
        Know("cpu-pkg", 55);
        Bridge();

        PhoneSets(Change("CPU-PKG", threshold: 88.5, severity: AlertSeverity.Warning, direction: AlertDirection.Below));

        _store.TryGet("cpu-pkg", out var rule).Should().BeTrue();
        rule!.SensorName.Should().Be("cpu-pkg", "the rule is keyed by the PC's own spelling of the sensor");
        rule.Threshold.Should().Be(88.5);
        rule.Direction.Should().Be(AlertDirection.Below);
        rule.Severity.Should().Be(AlertSeverity.Warning);

        var published = _alerts.Rules.Should().ContainSingle().Subject;
        var shown = published.Should().ContainSingle().Subject;
        shown.SensorName.Should().Be("cpu-pkg");
        shown.Unit.Should().Be("°C");
        shown.CurrentValue.Should().Be(55);
        shown.Threshold.Should().Be(88.5);
    }

    [Fact]
    public void APhoneSetIsSavedAndShowsInThePcsOwnAlertsList()
    {
        Know("cpu-pkg");
        var pcList = new SensorAlertsSectionViewModel(_store, _tracker, _canvas);
        pcList.Rows.Should().BeEmpty();
        Bridge();
        var savesBefore = _canvas.AlertStoreSaveCount;

        PhoneSets(Change("cpu-pkg"));

        _canvas.AlertStoreSaveCount.Should().Be(savesBefore + 1, "the PC's profile save path ran, so the rule survives a restart");
        pcList.Rows.Should().ContainSingle(row => row.SensorName == "cpu-pkg");
    }

    [Fact]
    public void APhoneSetReplacesTheExistingRuleInsteadOfAddingASecond()
    {
        Know("cpu-pkg");
        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert { SensorName = "cpu-pkg", Threshold = 70 });
        Bridge();

        PhoneSets(Change("cpu-pkg", threshold: 95));

        _store.All.Should().ContainSingle().Which.Threshold.Should().Be(95);
    }

    [Fact]
    public void APhoneRemoveDeletesTheRuleAndPublishesTheRest()
    {
        Know("cpu-pkg");
        Know("gpu-temp");
        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert { SensorName = "cpu-pkg", Threshold = 70 });
        _canvas.ApplySensorAlert("gpu-temp", new SensorAlert { SensorName = "gpu-temp", Threshold = 80 });
        Bridge();
        var pcList = new SensorAlertsSectionViewModel(_store, _tracker, _canvas);

        PhoneRemoves("CPU-PKG");

        _store.TryGet("cpu-pkg", out _).Should().BeFalse();
        _store.TryGet("gpu-temp", out _).Should().BeTrue();
        _alerts.Rules.Should().ContainSingle().Which.Should().ContainSingle().Which.SensorName.Should().Be("gpu-temp");
        pcList.Rows.Should().ContainSingle(row => row.SensorName == "gpu-temp");
    }

    [Fact]
    public void ARemoveForASensorWithNoRuleChangesNothingButStillAnswers()
    {
        Know("cpu-pkg");
        Bridge();

        PhoneRemoves("cpu-pkg");

        _store.All.Should().BeEmpty();
        _alerts.Rules.Should().ContainSingle();
    }

    [Fact]
    public void AGetIsAnsweredWithTheCurrentRules()
    {
        Know("cpu-pkg", 61);
        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert { SensorName = "cpu-pkg", Threshold = 90 });
        Bridge();
        _alerts.Rules.Clear();

        _alerts.Raise(new PhoneSensorAlertRequest(PhoneSensorAlertRequestKind.Get, "phone-1"));

        _alerts.Rules.Should().ContainSingle().Which.Should().ContainSingle().Which.CurrentValue.Should().Be(61);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void ASetForASensorThePcDoesNotKnowIsRefusedAndTheRulesAreRepublished()
    {
        Know("cpu-pkg");
        Bridge();

        PhoneSets(Change("no-such-sensor"));

        _store.All.Should().BeEmpty();
        _alerts.Rules.Should().ContainSingle("the phone toggled optimistically and has to be told the PC kept its rules")
            .Which.Should().BeEmpty();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    public void ASetWithABadThresholdIsRefusedEvenIfItReachesTheDesktop(double threshold)
    {
        Know("cpu-pkg");
        Bridge();

        PhoneSets(Change("cpu-pkg", threshold: threshold));

        _store.All.Should().BeEmpty();
        _alerts.Rules.Should().ContainSingle();
    }

    [Fact]
    public void ASetWithAnUndefinedDirectionIsRefused()
    {
        Know("cpu-pkg");
        Bridge();

        PhoneSets(Change("cpu-pkg", direction: (AlertDirection)7));

        _store.All.Should().BeEmpty();
    }

    [Fact]
    public void TheRuleCountIsCappedForANewSensorButNotForAnEdit()
    {
        for (var i = 0; i < SensorAlertValidation.MaxRules; i++)
        {
            Know($"sensor-{i}");
            _store.Set(new SensorAlert { SensorName = $"sensor-{i}", Threshold = i });
        }

        Know("one-too-many");
        Bridge();

        PhoneSets(Change("one-too-many"));
        _store.All.Should().HaveCount(SensorAlertValidation.MaxRules);
        _store.TryGet("one-too-many", out _).Should().BeFalse();

        PhoneSets(Change("sensor-3", threshold: 1234));
        _store.TryGet("sensor-3", out var edited).Should().BeTrue();
        edited!.Threshold.Should().Be(1234);
        _store.All.Should().HaveCount(SensorAlertValidation.MaxRules);
    }

    [Fact]
    public void ARuleOnASensorThePcCanNoLongerSeeCanStillBeEditedAndRemoved()
    {
        _store.Set(new SensorAlert { SensorName = "ghost", Threshold = 5 });
        Bridge();

        PhoneSets(Change("ghost", threshold: 6));
        _store.TryGet("ghost", out var edited).Should().BeTrue();
        edited!.Threshold.Should().Be(6);

        PhoneRemoves("ghost");
        _store.All.Should().BeEmpty();
    }

    // ── Rules changed on the PC ──────────────────────────────────────────────

    [Fact]
    public void ARuleChangedOnThePcIsPublishedOnce()
    {
        Know("cpu-pkg");
        Bridge();

        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert { SensorName = "cpu-pkg", Threshold = 90 });

        _alerts.Rules.Should().ContainSingle().Which.Should().ContainSingle().Which.SensorName.Should().Be("cpu-pkg");
    }

    [Fact]
    public void APhoneEditIsPublishedOnceNotTwice()
    {
        Know("cpu-pkg");
        Bridge();

        PhoneSets(Change("cpu-pkg"));

        _alerts.Rules.Should().ContainSingle("the store's own change event must not add a second broadcast");
    }

    [Fact]
    public void AfterDisposeNothingIsSentAndARequestChangesNothing()
    {
        Know("cpu-pkg");
        _canvas.ApplySensorAlert("cpu-pkg", new SensorAlert { SensorName = "cpu-pkg", Threshold = 90 });
        Bridge().Dispose();

        _canvas.ApplyTelemetry(Reading("cpu-pkg", 99));
        PhoneSets(Change("cpu-pkg", threshold: 10));
        _store.Set(new SensorAlert { SensorName = "cpu-pkg", Threshold = 91 });

        _alerts.Fired.Should().BeEmpty();
        _alerts.Rules.Should().BeEmpty();
        _store.TryGet("cpu-pkg", out var rule).Should().BeTrue();
        rule!.Threshold.Should().Be(91);
    }

    private sealed class FakeAlerts : IPhoneSensorAlerts
    {
        public List<SensorAlertFiredEvent> Fired { get; } = [];

        public List<IReadOnlyList<SensorAlertRule>> Rules { get; } = [];

        public event Action<PhoneSensorAlertRequest>? PhoneRequested;

        public Task PublishFiredAsync(SensorAlertFiredEvent fired)
        {
            Fired.Add(fired);
            return Task.CompletedTask;
        }

        public Task PublishRulesAsync(IReadOnlyList<SensorAlertRule> rules)
        {
            Rules.Add(rules);
            return Task.CompletedTask;
        }

        public void RequestFromPhone(PhoneSensorAlertRequest request) => PhoneRequested?.Invoke(request);

        public void Raise(PhoneSensorAlertRequest request) => PhoneRequested?.Invoke(request);
    }
}
