using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Behaviour coverage for <see cref="SensorAlertsSectionViewModel"/>, the Settings "Sensor alerts"
/// card view model (RemEx-8wpvr.5): rows following the store and tracker, sort order, the composed
/// summary/tripped text, Acknowledge all's CanExecute, Remove, and a Reset all that only clears
/// after a confirmation. Uses a fake <see cref="ISensorCatalog"/> rather than
/// <c>CanvasDashboardViewModel</c> — the section is required to never reference the canvas view
/// model directly, so testing against the seam is the correct isolation, not a shortcut.
/// </summary>
/// <remarks>
/// Plus source-scrape guards, since there is no headless render for this app (see the class
/// remarks on <c>ButtonVocabularyTests</c>): <c>SettingsView.axaml</c> binds the section through a
/// virtualising <c>ListBox</c> with a <c>MaxHeight</c>, Reset goes through
/// <c>ConfirmationDialogHost</c>, and all ten new keys are defined in every one of the nine resx
/// files.
/// </remarks>
public class SensorAlertsSectionViewModelTests
{
    private static SensorAlert MakeAlert(
        string name,
        double threshold = 90,
        AlertDirection direction = AlertDirection.Above,
        AlertSeverity severity = AlertSeverity.Critical) => new()
    {
        SensorName = name,
        Threshold = threshold,
        Direction = direction,
        Severity = severity,
    };

    private sealed class FakeSensorCatalog : ISensorCatalog
    {
        private readonly Dictionary<string, SensorInfo> _sensors = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<SensorInfo> Known => _sensors.Values.ToList();

        public void Add(SensorInfo info) => _sensors[info.Name] = info;

        public bool TryResolve(string name, [MaybeNullWhen(false)] out SensorInfo info)
            => _sensors.TryGetValue(name, out info);
    }

    // ─────────────────────────── rows: follow the store, sort, summary ───────────────────────────

    [Fact]
    public void NewSection_WithNoAlerts_IsEmpty()
    {
        var vm = new SensorAlertsSectionViewModel(new SensorAlertStore(), new SensorAlertTracker(), new FakeSensorCatalog());

        vm.IsEmpty.Should().BeTrue();
        vm.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Rows_FollowStoreChanges_SortedByDisplayName()
    {
        var store = new SensorAlertStore();
        var catalog = new FakeSensorCatalog();
        catalog.Add(new SensorInfo("cpu-pkg-0", "CPU Package", "°C", true));
        catalog.Add(new SensorInfo("gpu-hot-0", "GPU Hot Spot", "°C", true));
        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), catalog);

        // Inserted GPU first — the row order must come from DisplayName, not insertion order.
        store.Set(MakeAlert("gpu-hot-0"));
        store.Set(MakeAlert("cpu-pkg-0"));

        vm.IsEmpty.Should().BeFalse();
        vm.Rows.Select(r => r.DisplayName).Should().Equal("CPU Package", "GPU Hot Spot");

        store.Remove("cpu-pkg-0");
        store.Remove("gpu-hot-0");

        vm.Rows.Should().BeEmpty();
        vm.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Summary_IsComposedFromLocalizedDirectionSeverityAndFormatReading()
    {
        var store = new SensorAlertStore();
        var catalog = new FakeSensorCatalog();
        catalog.Add(new SensorInfo("cpu-pkg-0", "CPU Package", "°C", true));
        store.Set(MakeAlert("cpu-pkg-0", threshold: 90, direction: AlertDirection.Above, severity: AlertSeverity.Critical));

        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), catalog);

        var direction = LocalizationService.Instance["AlertDirection_Above"];
        var severity = LocalizationService.Instance["AlertSeverity_Critical"];
        var threshold = SensorReadingFormat.FormatReading(90, "°C");
        var expected = string.Format(
            LocalizationService.Instance["Canvas_AlertBellConfigured"], direction, threshold, severity);

        vm.Rows.Single().Summary.Should().Be(expected,
            "the settings row and the canvas bell tooltip must read identically in every language");
    }

    [Fact]
    public void UnresolvableSensor_ShowsTheStoredNameAndIsDisconnected()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("ghost-sensor"));

        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), new FakeSensorCatalog());

        var row = vm.Rows.Single();
        row.DisplayName.Should().Be("ghost-sensor", "the catalog has never heard of this sensor");
        row.IsDisconnected.Should().BeTrue();
    }

    [Fact]
    public void ResolvedButNotReporting_IsAlsoDisconnected()
    {
        var store = new SensorAlertStore();
        var catalog = new FakeSensorCatalog();
        catalog.Add(new SensorInfo("cpu-pkg-0", "CPU Package", "°C", IsConnected: false));
        store.Set(MakeAlert("cpu-pkg-0"));

        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), catalog);

        var row = vm.Rows.Single();
        row.DisplayName.Should().Be("CPU Package", "the catalog does know this sensor's name");
        row.IsDisconnected.Should().BeTrue("the catalog says it is not currently reporting");
    }

    // ─────────────────────────── tripped state ───────────────────────────

    [Fact]
    public void TrippedSummary_ReflectsTheTracker_AndClearsOnAcknowledge()
    {
        var store = new SensorAlertStore();
        var tracker = new SensorAlertTracker();
        var catalog = new FakeSensorCatalog();
        catalog.Add(new SensorInfo("cpu-pkg-0", "CPU Package", "°C", true));
        var alert = MakeAlert("cpu-pkg-0");
        store.Set(alert);

        var vm = new SensorAlertsSectionViewModel(store, tracker, catalog);
        var row = vm.Rows.Single();
        row.IsTripped.Should().BeFalse();
        row.TrippedSummary.Should().BeEmpty();

        var at = new DateTimeOffset(2026, 1, 1, 8, 30, 0, TimeSpan.Zero);
        tracker.Trip("cpu-pkg-0", 91.2, alert, at);

        row.IsTripped.Should().BeTrue();
        var expected = string.Format(
            LocalizationService.Instance["Settings_Alerts_TrippedAt"],
            at.LocalDateTime.ToString("t", LocalizationService.Instance.Culture),
            SensorReadingFormat.FormatReading(91.2, "°C"));
        row.TrippedSummary.Should().Be(expected);
        vm.TrippedCount.Should().Be(1);

        tracker.Acknowledge("cpu-pkg-0");

        row.IsTripped.Should().BeFalse();
        row.TrippedSummary.Should().BeEmpty();
        vm.TrippedCount.Should().Be(0);
    }

    [Fact]
    public void AcknowledgeAllCommand_CanExecuteFollowsTrippedCount()
    {
        var store = new SensorAlertStore();
        var tracker = new SensorAlertTracker();
        var alert = MakeAlert("cpu-pkg-0");
        store.Set(alert);

        var vm = new SensorAlertsSectionViewModel(store, tracker, new FakeSensorCatalog());
        vm.AcknowledgeAllCommand.CanExecute(null).Should().BeFalse("nothing is tripped yet");

        tracker.Trip("cpu-pkg-0", 91.2, alert);
        vm.AcknowledgeAllCommand.CanExecute(null).Should().BeTrue();

        vm.AcknowledgeAllCommand.Execute(null);

        tracker.TrippedCount.Should().Be(0, "Acknowledge all must call the tracker, not just clear local state");
        vm.AcknowledgeAllCommand.CanExecute(null).Should().BeFalse();
    }

    // ─────────────────────────── Remove ───────────────────────────

    [Fact]
    public void RemoveCommand_GoesThroughTheStore()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("cpu-pkg-0"));
        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), new FakeSensorCatalog());
        var row = vm.Rows.Single();

        row.RemoveCommand.Execute(null);

        store.TryGet("cpu-pkg-0", out _).Should().BeFalse();
        vm.IsEmpty.Should().BeTrue();
    }

    // ─────────────────────────── Reset all: fail-closed confirmation ───────────────────────────

    [Fact]
    public async Task ResetAll_WithNoDialogWired_DeclinesRatherThanClearing()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("cpu-pkg-0"));
        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), new FakeSensorCatalog());

        // OnConfirmationRequested deliberately left null — the unwired-ViewModel case.
        await vm.ResetAllCommand.ExecuteAsync(null);

        store.All.Should().HaveCount(1, "an unwired section must decline a destructive action, not perform it unconfirmed");
    }

    [Fact]
    public async Task ResetAll_WhenUserDeclines_DoesNotClear()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("cpu-pkg-0"));
        store.Set(MakeAlert("gpu-hot-0"));
        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), new FakeSensorCatalog())
        {
            OnConfirmationRequested = (_, _, _) => Task.FromResult(false),
        };

        await vm.ResetAllCommand.ExecuteAsync(null);

        store.All.Should().HaveCount(2, "a delegate returning false must have the same effect as no delegate at all");
    }

    [Fact]
    public async Task ResetAll_WhenUserConfirms_ClearsEverything()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("cpu-pkg-0"));
        store.Set(MakeAlert("gpu-hot-0"));
        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), new FakeSensorCatalog())
        {
            OnConfirmationRequested = (_, _, _) => Task.FromResult(true),
        };

        await vm.ResetAllCommand.ExecuteAsync(null);

        store.All.Should().BeEmpty("a confirmed Reset all must actually clear the store");
        vm.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task ResetAll_PassesTheCountIntoTheConfirmationBody()
    {
        var store = new SensorAlertStore();
        store.Set(MakeAlert("cpu-pkg-0"));
        store.Set(MakeAlert("gpu-hot-0"));

        string? capturedTitle = null;
        string? capturedMessage = null;
        var vm = new SensorAlertsSectionViewModel(store, new SensorAlertTracker(), new FakeSensorCatalog())
        {
            OnConfirmationRequested = (title, message, _) =>
            {
                capturedTitle = title;
                capturedMessage = message;
                return Task.FromResult(false);
            },
        };

        await vm.ResetAllCommand.ExecuteAsync(null);

        capturedTitle.Should().Be(LocalizationService.Instance["Settings_Alerts_ResetConfirmTitle"]);
        capturedMessage.Should().Be(
            string.Format(LocalizationService.Instance["Settings_Alerts_ResetConfirmBody"], 2));
    }

    // ─────────────────────────── XAML / code-behind source-scrape guards ───────────────────────────

    [Fact]
    public void SettingsViewBindsTheAlertsSectionThroughAVirtualisingListBoxWithMaxHeight()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml"));

        text.Should().Contain("DataContext=\"{Binding AlertsSection}\"",
            "the alerts card must bind SensorAlertsSectionViewModel, not read AlertsSection.* piecemeal");

        var listBox = System.Text.RegularExpressions.Regex.Match(
            text, @"<ListBox\s+ItemsSource=""\{Binding Rows\}""[^>]*>");
        listBox.Success.Should().BeTrue("the alert rows must be a ListBox bound to Rows");
        listBox.Value.Should().Contain("MaxHeight",
            "a ListBox with no MaxHeight lets the card grow without bound as alerts are added");
        text.Should().NotContain("<ItemsControl ItemsSource=\"{Binding Rows}\"",
            "a bare ItemsControl realizes every row instead of virtualising (bd memory)");
    }

    [Fact]
    public void ResetAllGoesThroughConfirmationDialogHost()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml.cs"));

        text.Should().Contain("AlertsSection.OnConfirmationRequested = ConfirmationDialogHost.For(this)",
            "Reset all must be guarded the same way every other destructive Settings action is (RemEx-6p1f)");
    }

    private static readonly string[] NewKeys =
    [
        "Settings_Alerts_Title",
        "Settings_Alerts_Empty",
        "Settings_Alerts_Edit",
        "Settings_Alerts_CopyTo",
        "Settings_Alerts_Remove",
        "Settings_Alerts_AcknowledgeAll",
        "Settings_Alerts_ResetAll",
        "Settings_Alerts_ResetConfirmTitle",
        "Settings_Alerts_ResetConfirmBody",
        "Settings_Alerts_TrippedAt",
    ];

    [Fact]
    public void AllTenAlertsKeysAreDefinedInAllNineResxFiles()
    {
        var localeDirectory = Path.Combine(RepoRoot(), "remex.desktop", "Localization");
        var resxFiles = Directory.GetFiles(localeDirectory, "Strings*.resx");
        resxFiles.Should().HaveCountGreaterOrEqualTo(9, "the base resx plus 8 locale variants must all be on disk");

        var missing = new List<string>();
        foreach (var path in resxFiles)
        {
            var defined = XDocument.Load(path)
                .Root!
                .Elements("data")
                .Select(d => (string?)d.Attribute("name"))
                .Where(name => !string.IsNullOrEmpty(name))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var key in NewKeys)
            {
                if (!defined.Contains(key))
                    missing.Add($"{key} missing from {Path.GetFileName(path)}");
            }
        }

        missing.Should().BeEmpty(
            "a key missing from even one locale renders as its own name in that language "
            + "(LocalizationService's indexer ends in '?? key')");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
