using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-8wpvr.3: the Sensors badge switched from counting alerts fired this session to mirroring
/// <see cref="SensorAlertTracker.TrippedCount"/>, <c>NavigateToCanvas</c> stopped clearing it, and
/// every notify-worthy trip now reaches <see cref="NotificationService"/> instead of only being
/// recorded in <c>AlertNotifications</c>. These pin the WIRING: that the badge tracks the same
/// tracker the canvas trips, that navigation no longer acknowledges anything, that dismissing does,
/// and that a trip's severity maps to the importance the tray/toast surface will act on.
/// </summary>
/// <remarks>
/// <see cref="ShellViewModel.OnSensorAlertFired"/> is <c>internal</c> rather than <c>private</c>
/// specifically so this class can drive it directly, the same way
/// <c>ShellTransferAndDiagnosticsBadgeTests</c> drives badge wiring without a real telemetry tick -
/// going through a real <see cref="Remex.Desktop.ViewModels.CanvasDashboardViewModel"/> trip would
/// require awaiting <c>InitializeAsync</c>, which needs a dispatcher this assembly does not pump.
/// </remarks>
public sealed class ShellAlertNotificationTests : IAsyncLifetime, IDisposable
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private readonly SensorAlertTracker _alertTracker;
    private readonly IServiceProvider _serviceProvider;
    private ShellViewModel _shell = null!;

    private readonly List<(NotificationImportance Importance, string Title, string Message)> _announced = [];
    private readonly IInAppNotificationSink? _savedInApp = NotificationService.Instance.InApp;
    private readonly Func<bool>? _savedProbe = NotificationService.Instance.WindowVisibleProbe;
    private readonly Action<Action> _savedDispatch = NotificationService.Instance.Dispatch;

    public ShellAlertNotificationTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-8wpvr3-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);

        _serviceProvider = new ServiceCollection().AddLogging()
            .AddSingleton<SensorAlertStore>()
            .AddSingleton<SensorAlertTracker>()
            .BuildServiceProvider();
        // Same instance ShellViewModel's constructor resolves - resolving it here too is what lets a
        // test drive a trip without going through the canvas.
        _alertTracker = _serviceProvider.GetRequiredService<SensorAlertTracker>();

        NotificationService.Instance.Dispatch = static work => work();
        NotificationService.Instance.WindowVisibleProbe = () => true;
        NotificationService.Instance.InApp = new RecordingSink(_announced);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();

        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new ConnectionViewModel(),
            _serviceProvider,
            transferQueuePost: action => action());

        _shell.ProfileReplacedDispatch = run => run();
        _shell.DiagnosticsLogDispatch = run => run();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        NotificationService.Instance.InApp = _savedInApp;
        NotificationService.Instance.WindowVisibleProbe = _savedProbe;
        NotificationService.Instance.Dispatch = _savedDispatch;
        GC.SuppressFinalize(this);
    }

    private static SensorAlert MakeAlert(string sensorName, AlertSeverity severity, AlertDirection direction = AlertDirection.Above) =>
        new() { SensorName = sensorName, Threshold = 80, Direction = direction, Severity = severity };

    [Fact]
    public void ACriticalTripNotifiesAsAProblem()
    {
        var alert = MakeAlert("CPU Temp", AlertSeverity.Critical);

        _shell.OnSensorAlertFired(alert, 92.4);

        _announced.Should().ContainSingle("a notify-worthy trip must reach the notification surface");
        _announced[0].Importance.Should().Be(NotificationImportance.Problem,
            "Critical alerts must carry enough urgency to interrupt through a tray balloon");
    }

    [Fact]
    public void AWarningTripNotifiesAsAnOutcome()
    {
        var alert = MakeAlert("RAM Usage", AlertSeverity.Warning);

        _shell.OnSensorAlertFired(alert, 88.0);

        _announced.Should().ContainSingle();
        _announced[0].Importance.Should().Be(NotificationImportance.Outcome,
            "Warning alerts are worth a toast, not the interruption Problem carries");
    }

    [Fact]
    public void AnUnresolvedSensorFallsBackToItsRawNameAndAnEmptyUnit()
    {
        // No sensor card exists in this fixture's canvas, so ISensorCatalog.TryResolve must fail and
        // OnSensorAlertFired must fall back rather than throw or leave a placeholder in the text.
        var alert = MakeAlert("Unmapped Sensor", AlertSeverity.Warning);

        _shell.OnSensorAlertFired(alert, 42.0);

        _announced.Should().ContainSingle();
        _announced[0].Title.Should().Contain("Unmapped Sensor");
    }

    [Fact]
    public void BodyStatesTheThresholdWithUnitNotTheReading()
    {
        // Give the canvas a resolvable sensor so unit formatting is exercised end to end - the
        // unmapped-sensor fixture below covers the empty-unit case instead.
        var placedSensor = new SensorViewModel();
        placedSensor.Update(new SensorReading { Id = "cpu-temp-0", Name = "CPU Temp", Value = 92.4, Unit = "°C" });
        _shell.CanvasViewModel!.Cards.Add(new CanvasCardViewModel { CardType = "Sensor", Sensor = placedSensor });

        var alert = MakeAlert("CPU Temp", AlertSeverity.Critical); // Threshold = 80

        _shell.OnSensorAlertFired(alert, 92.4);

        _announced.Should().ContainSingle();
        _announced[0].Title.Should().Contain("92.4 °C",
            "the title states the reading, with a space between value and unit");
        _announced[0].Message.Should().Contain("80.0 °C",
            "the body states the threshold that was crossed, not the reading");
        _announced[0].Message.Should().NotContain("92.4",
            "the body must not restate the reading — that's the title's job, or it reads as a tautology");
    }

    [Fact]
    public void AnEmptyUnitYieldsNoTrailingSpaceInTheReading()
    {
        // "Unmapped Sensor" resolves to nothing in this fixture's canvas, so unit is empty.
        var alert = MakeAlert("Unmapped Sensor", AlertSeverity.Warning);

        _shell.OnSensorAlertFired(alert, 42.0);

        _announced.Should().ContainSingle();
        _announced[0].Title.Should().EndWith("42.0",
            "an empty unit must not leave a trailing space after the formatted value");
    }

    [Fact]
    public void TheBadgeMirrorsTheTrackedCountRatherThanCountingFiredAlerts()
    {
        _shell.AlertBadgeCount.Should().Be(0);

        _alertTracker.Trip("CPU Temp", 92.4, MakeAlert("CPU Temp", AlertSeverity.Critical));
        _shell.AlertBadgeCount.Should().Be(1);
        _shell.HasAlerts.Should().BeTrue();

        _alertTracker.Trip("RAM Usage", 88.0, MakeAlert("RAM Usage", AlertSeverity.Warning));
        _shell.AlertBadgeCount.Should().Be(2, "two distinct sensors are tripped and unacknowledged");

        _alertTracker.Acknowledge("CPU Temp");
        _shell.AlertBadgeCount.Should().Be(1, "acknowledging one sensor leaves the other tripped");
    }

    [Fact]
    public void NavigatingToTheCanvasLeavesTheCountUntouched()
    {
        _alertTracker.Trip("CPU Temp", 92.4, MakeAlert("CPU Temp", AlertSeverity.Critical));
        _shell.AlertBadgeCount.Should().Be(1);

        _shell.NavigateToCanvasCommand.Execute(null);

        _shell.AlertBadgeCount.Should().Be(1,
            "opening the canvas must not acknowledge a sensor that is still tripped");
    }

    [Fact]
    public void DismissingAcknowledgesEveryTrippedSensor()
    {
        _alertTracker.Trip("CPU Temp", 92.4, MakeAlert("CPU Temp", AlertSeverity.Critical));
        _alertTracker.Trip("RAM Usage", 88.0, MakeAlert("RAM Usage", AlertSeverity.Warning));
        _shell.AlertBadgeCount.Should().Be(2);

        _shell.DismissAlertsCommand.Execute(null);

        _shell.AlertBadgeCount.Should().Be(0, "dismiss is the explicit acknowledgement of every trip");
        _shell.HasAlerts.Should().BeFalse();
        _alertTracker.TrippedCount.Should().Be(0);
    }

    private sealed class RecordingSink(List<(NotificationImportance, string, string)> announced)
        : IInAppNotificationSink
    {
        public void Show(NotificationImportance importance, string title, string message)
            => announced.Add((importance, title, message));
    }
}
