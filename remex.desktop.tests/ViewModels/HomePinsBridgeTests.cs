using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Models;
using Remex.Core.Services.Home;
using Remex.Core.Validation;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The desktop end of the PC Home's pinned-sensor sync (RemEx-wqo7a.5): a phone's pin lands on the
/// canvas card and survives the next save, a pin the PC cannot honour is refused and answered, a pin
/// changed on the PC reaches the store, and a republish that changed nothing stays off the wire.
/// </summary>
/// <remarks>
/// <para>
/// A REAL <see cref="ShellViewModel"/>, because <c>SetCardPinned</c> reads <c>_shell.CurrentView</c>
/// and the canvas cannot be driven through it with the <c>null!</c> shell the lighter canvas tests
/// use. Built the same way as <c>ProfileReplacementInvalidatesCustomizationVmTests</c>.
/// </para>
/// <para>
/// THE STORE IS A TEST DOUBLE that keeps the contract the host's <c>HomePinnedSensorsStore</c> keeps
/// (normalize, no-op when unchanged, the first publish after a phone request always broadcasts):
/// this assembly cannot reference <c>Remex.Agent</c>. The host's own store is pinned by
/// <c>HomePinnedSensorsStoreTests</c> in <c>remex.agent.tests</c>.
/// </para>
/// </remarks>
public sealed class HomePinsBridgeTests : IAsyncLifetime
{
    private static readonly MethodInfo TriggerSaveMethod =
        typeof(CanvasDashboardViewModel).GetMethod("TriggerSave", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private readonly FakeStore _store = new();
    private ShellViewModel _shell = null!;
    private HomePinsBridge? _bridge;

    public HomePinsBridgeTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-homepins-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();
        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .BuildServiceProvider());
    }

    public Task DisposeAsync()
    {
        _bridge?.Dispose();
        _shell.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }

    private CanvasDashboardViewModel Canvas => _shell.CanvasViewModel!;

    private static CanvasCardViewModel SensorCard(string name) =>
        new() { CardType = "Sensor", Sensor = new SensorViewModel { Name = name } };

    private CanvasCardViewModel Place(string name)
    {
        var card = SensorCard(name);
        Canvas.Cards.Add(card);
        return card;
    }

    private HomePinsBridge Bridge() => _bridge = new HomePinsBridge(_store, _layoutService, Canvas, post: run => run());

    private List<string> SavedPins => _layoutService.CurrentProfile.PinnedSensorIds ?? [];

    [Fact]
    public void APhonePinSetsTheCardsPinAndSurvivesALaterSave()
    {
        var card = Place("CPU Temp");
        Bridge();

        _store.RequestFromPhone(new HomePinChange { SensorName = "cpu temp", Pinned = true }, "phone-1");

        card.IsPinnedToHome.Should().BeTrue("the canvas card is what the next save treats as the truth");
        SavedPins.Should().Contain("CPU Temp");

        // The merge in TriggerSave rebuilds the pinned list from the live cards; a pin that only
        // touched the profile would be gone after this.
        TriggerSaveMethod.Invoke(Canvas, null);

        SavedPins.Should().ContainSingle(id => id == "CPU Temp");
        _store.Current.SensorNames.Should().Equal("CPU Temp");
    }

    [Fact]
    public void APhoneUnpinClearsEveryCardForThatSensor()
    {
        var first = Place("CPU Temp");
        var second = Place("CPU Temp");
        second.PositionY = first.PositionY + 400;
        first.IsPinnedToHome = true;
        second.IsPinnedToHome = true;
        TriggerSaveMethod.Invoke(Canvas, null);
        Bridge();

        _store.RequestFromPhone(new HomePinChange { SensorName = "CPU Temp", Pinned = false }, "phone-1");
        TriggerSaveMethod.Invoke(Canvas, null);

        first.IsPinnedToHome.Should().BeFalse();
        second.IsPinnedToHome.Should().BeFalse("a pinned second card would pin the sensor straight back on save");
        SavedPins.Should().NotContain("CPU Temp");
        _store.Current.SensorNames.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Fan 3")]     // staged, not placed
    [InlineData("No Such")]   // not on the canvas at all
    public void ASensorWithNoPlacedCardIsRefusedAndTheListIsRepublished(string name)
    {
        Place("CPU Temp");
        Canvas.StagedCards.Add(SensorCard("Fan 3"));
        Bridge();
        var revisionBefore = _store.Current.Revision;
        var raisedBefore = _store.ChangedCount;

        _store.RequestFromPhone(new HomePinChange { SensorName = name, Pinned = true }, "phone-1");

        SavedPins.Should().NotContain(name);
        _store.ChangedCount.Should().Be(raisedBefore + 1,
            "the phone toggled optimistically and has to be told the PC kept its list");
        _store.Current.Revision.Should().BeGreaterThan(revisionBefore);
        _store.Current.SensorNames.Should().NotContain(name);
        _store.Current.PinnableSensorNames.Should().Equal("CPU Temp");
    }

    [Fact]
    public async Task APinFromThePcChecklistIsPublished()
    {
        Place("CPU Temp");
        Bridge();
        var layout = new LayoutSettingsViewModel(_layoutService, Canvas, home: null);
        await layout.InitializeAsync();

        layout.PinnedSensors.Single().IsPinned = true;

        // Published on ProfileSaved, which follows the layout service's debounced write.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_store.Current.SensorNames.Contains("CPU Temp") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        _store.Current.SensorNames.Should().Equal("CPU Temp");
    }

    [Fact]
    public void APhonePinShowsInTheLayoutChecklist()
    {
        Place("CPU Temp");
        Bridge();
        var layout = new LayoutSettingsViewModel(_layoutService, Canvas, home: null);
        layout.RefreshSensors();
        layout.PinnedSensors.Single().IsPinned.Should().BeFalse();

        _store.RequestFromPhone(new HomePinChange { SensorName = "CPU Temp", Pinned = true }, "phone-1");

        layout.PinnedSensors.Single().IsPinned.Should().BeTrue();
    }

    [Fact]
    public void ACardArrivingOnTheCanvasBecomesPinnable()
    {
        Bridge();
        _store.Current.PinnableSensorNames.Should().BeEmpty();

        Place("GPU Temp");
        Place("CPU Temp");

        _store.Current.PinnableSensorNames.Should().Equal("CPU Temp", "GPU Temp");
    }

    [Fact]
    public async Task ARepublishThatChangedNothingIsNotRebroadcast()
    {
        Place("CPU Temp");
        Bridge();
        var publishesBefore = _store.PublishCount;
        var raisedBefore = _store.ChangedCount;

        // SaveAsync always writes and raises ProfileSaved, so the bridge publishes again.
        await _layoutService.SaveAsync(_layoutService.CurrentProfile);

        _store.PublishCount.Should().BeGreaterThan(publishesBefore, "the bridge must have republished");
        _store.ChangedCount.Should().Be(raisedBefore, "an unchanged list is not news for the phone");
    }

    [Fact]
    public void AfterDisposeAPhoneRequestChangesNothing()
    {
        var card = Place("CPU Temp");
        Bridge().Dispose();

        _store.RequestFromPhone(new HomePinChange { SensorName = "CPU Temp", Pinned = true }, "phone-1");

        card.IsPinnedToHome.Should().BeFalse();
    }

    /// <summary>The host store's contract, without the host.</summary>
    private sealed class FakeStore : IHomePinnedSensorsStore
    {
        private bool _answerOwed;

        public HomePinnedSensors Current { get; private set; } = new();

        public int PublishCount { get; private set; }

        public int ChangedCount { get; private set; }

        public event Action<HomePinnedSensors>? Changed;

        public event Action<HomePinChange, string>? PhoneChangeRequested;

        public bool PublishFromPc(IReadOnlyList<string> pinned, IReadOnlyList<string> pinnable)
        {
            PublishCount++;
            var p = HomePinsValidation.NormalizeNames(pinned);
            var q = HomePinsValidation.NormalizeNames(pinnable);
            if (Current.Revision > 0 && !_answerOwed
                && p.SequenceEqual(Current.SensorNames, StringComparer.Ordinal)
                && q.SequenceEqual(Current.PinnableSensorNames, StringComparer.Ordinal))
            {
                return false;
            }

            _answerOwed = false;
            Current = new HomePinnedSensors
            {
                SensorNames = p,
                PinnableSensorNames = q,
                Revision = Current.Revision + 1,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            ChangedCount++;
            Changed?.Invoke(Current);
            return true;
        }

        public void RequestFromPhone(HomePinChange change, string clientId)
        {
            _answerOwed = true;
            PhoneChangeRequested?.Invoke(change, clientId);
        }
    }
}
