using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Behaviour coverage for <see cref="CopyAlertDialogViewModel"/>, the Copy-to picker (RemEx-8wpvr.6):
/// same-unit-by-default candidate scoping, the show-all toggle, the filter, and Apply writing one
/// alert per selected candidate through <see cref="SensorAlertStore"/>. Uses a fake
/// <see cref="ISensorCatalog"/>, the same isolation <c>SensorAlertsSectionViewModelTests</c> uses,
/// rather than <c>CanvasDashboardViewModel</c>.
/// </summary>
/// <remarks>
/// Plus source-scrape guards, since there is no headless render for this app (see the class remarks
/// on <c>ButtonVocabularyTests</c>): <c>SettingsView.axaml.cs</c> wires <c>CopyRequested</c> to
/// <c>CopyAlertDialog</c>, the dialog markup carries exactly one <c>.primary</c> button, and all five
/// new keys are defined in every one of the nine resx files.
/// </remarks>
public class CopyAlertDialogViewModelTests
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

    private static (SensorAlertStore Store, FakeSensorCatalog Catalog) BuildFixture()
    {
        var store = new SensorAlertStore();
        var catalog = new FakeSensorCatalog();
        catalog.Add(new SensorInfo("cpu-pkg-0", "CPU Package", "°C", true));
        catalog.Add(new SensorInfo("gpu-hot-0", "GPU Hot Spot", "°C", true));
        catalog.Add(new SensorInfo("psu-fan-0", "PSU Fan", "RPM", true));
        return (store, catalog);
    }

    // ─────────────────────────── same-unit default / show-all ───────────────────────────

    [Fact]
    public void SourceUnitKnown_DefaultsToSameUnitOnly_AndScopesCandidates()
    {
        var (store, catalog) = BuildFixture();
        store.Set(MakeAlert("cpu-pkg-0"));

        var vm = new CopyAlertDialogViewModel(MakeAlert("cpu-pkg-0"), catalog, store);

        vm.SameUnitOnly.Should().BeTrue("cpu-pkg-0's unit (°C) is known");
        vm.SameUnitToggleEnabled.Should().BeTrue();
        vm.Candidates.Select(c => c.Name).Should().BeEquivalentTo(new[] { "gpu-hot-0" },
            "psu-fan-0 is RPM, not °C, so it is excluded by default");
    }

    [Fact]
    public void ShowAllSensors_TogglesSameUnitOnlyAndUnscoping()
    {
        var (store, catalog) = BuildFixture();
        var vm = new CopyAlertDialogViewModel(MakeAlert("cpu-pkg-0"), catalog, store);

        vm.ShowAllSensors.Should().BeFalse();
        vm.ShowAllSensors = true;

        vm.SameUnitOnly.Should().BeFalse();
        vm.Candidates.Select(c => c.Name).Should().BeEquivalentTo("gpu-hot-0", "psu-fan-0");
    }

    [Fact]
    public void SourceUnitUnknown_ForcesSameUnitOnlyFalse_AndDisablesTheToggle()
    {
        var (store, catalog) = BuildFixture();
        // "unknown-sensor" was never added to the catalog — TryResolve fails, unit is unknown.
        var vm = new CopyAlertDialogViewModel(MakeAlert("unknown-sensor"), catalog, store);

        vm.SameUnitOnly.Should().BeFalse("there is nothing to match same-unit against");
        vm.SameUnitToggleEnabled.Should().BeFalse();
        vm.Candidates.Select(c => c.Name).Should().BeEquivalentTo("cpu-pkg-0", "gpu-hot-0", "psu-fan-0");
    }

    [Fact]
    public void SourceIsExcludedFromCandidates()
    {
        var (store, catalog) = BuildFixture();
        var vm = new CopyAlertDialogViewModel(MakeAlert("cpu-pkg-0"), catalog, store);

        vm.ShowAllSensors = true; // widen scope so exclusion is the only thing under test
        vm.Candidates.Select(c => c.Name).Should().NotContain("cpu-pkg-0");
    }

    // ─────────────────────────── filter ───────────────────────────

    [Fact]
    public void Filter_IsCaseInsensitiveContainsOnDisplayName()
    {
        var (store, catalog) = BuildFixture();
        var vm = new CopyAlertDialogViewModel(MakeAlert("cpu-pkg-0"), catalog, store)
        {
            ShowAllSensors = true,
        };

        vm.Filter = "hot spot";
        vm.Candidates.Select(c => c.Name).Should().BeEquivalentTo("gpu-hot-0");

        vm.Filter = "PSU";
        vm.Candidates.Select(c => c.Name).Should().BeEquivalentTo("psu-fan-0");

        vm.Filter = string.Empty;
        vm.Candidates.Should().HaveCount(2);
    }

    // ─────────────────────────── header ───────────────────────────

    [Fact]
    public void Header_ReusesTheCanvasBellSummaryComposition()
    {
        var (store, catalog) = BuildFixture();
        var alert = MakeAlert("cpu-pkg-0", threshold: 90, direction: AlertDirection.Above, severity: AlertSeverity.Critical);

        var vm = new CopyAlertDialogViewModel(alert, catalog, store);

        var direction = LocalizationService.Instance["AlertDirection_Above"];
        var severity = LocalizationService.Instance["AlertSeverity_Critical"];
        var threshold = SensorReadingFormat.FormatReading(90, "°C");
        var summary = string.Format(
            LocalizationService.Instance["Canvas_AlertBellConfigured"], direction, threshold, severity);
        var expected = string.Format(LocalizationService.Instance["CopyAlert_Title"], "CPU Package", summary);

        vm.Header.Should().Be(expected);
    }

    // ─────────────────────────── Apply / Cancel ───────────────────────────

    [Fact]
    public void Apply_WritesOneAlertPerSelectedCandidate_CarryingTheSourceThresholdDirectionSeverity()
    {
        var (store, catalog) = BuildFixture();
        var source = MakeAlert("cpu-pkg-0", threshold: 90, direction: AlertDirection.Above, severity: AlertSeverity.Critical);
        var vm = new CopyAlertDialogViewModel(source, catalog, store) { ShowAllSensors = true };

        vm.SelectedCandidates.Add(vm.Candidates.Single(c => c.Name == "gpu-hot-0"));
        vm.SelectedCandidates.Add(vm.Candidates.Single(c => c.Name == "psu-fan-0"));

        var changedCount = 0;
        store.Changed += () => changedCount++;

        vm.ApplyCommand.Execute(null);

        store.TryGet("gpu-hot-0", out var gpuAlert).Should().BeTrue();
        gpuAlert.Should().BeEquivalentTo(source with { SensorName = "gpu-hot-0" });

        store.TryGet("psu-fan-0", out var psuAlert).Should().BeTrue();
        psuAlert.Should().BeEquivalentTo(source with { SensorName = "psu-fan-0" });

        changedCount.Should().Be(2, "store.Changed must fire once per Set");
    }

    [Fact]
    public void Apply_OverwritesAnExistingAlertOnTheCandidate()
    {
        var (store, catalog) = BuildFixture();
        store.Set(MakeAlert("gpu-hot-0", threshold: 50, direction: AlertDirection.Below, severity: AlertSeverity.Warning));

        var source = MakeAlert("cpu-pkg-0", threshold: 90, direction: AlertDirection.Above, severity: AlertSeverity.Critical);
        var vm = new CopyAlertDialogViewModel(source, catalog, store) { ShowAllSensors = true };
        vm.SelectedCandidates.Add(vm.Candidates.Single(c => c.Name == "gpu-hot-0"));

        vm.ApplyCommand.Execute(null);

        store.TryGet("gpu-hot-0", out var alert).Should().BeTrue();
        alert.Should().BeEquivalentTo(source with { SensorName = "gpu-hot-0" },
            "Apply must overwrite, not skip, a candidate that already has an alert");
    }

    [Fact]
    public void Apply_FiresRequestClose()
    {
        var (store, catalog) = BuildFixture();
        var vm = new CopyAlertDialogViewModel(MakeAlert("cpu-pkg-0"), catalog, store) { ShowAllSensors = true };
        vm.SelectedCandidates.Add(vm.Candidates.Single(c => c.Name == "gpu-hot-0"));

        var closed = 0;
        vm.RequestClose += () => closed++;

        vm.ApplyCommand.Execute(null);

        closed.Should().Be(1);
    }

    [Fact]
    public void Cancel_WritesNothing_ButStillFiresRequestClose()
    {
        var (store, catalog) = BuildFixture();
        store.Set(MakeAlert("cpu-pkg-0"));
        var vm = new CopyAlertDialogViewModel(MakeAlert("cpu-pkg-0"), catalog, store) { ShowAllSensors = true };
        vm.SelectedCandidates.Add(vm.Candidates.Single(c => c.Name == "gpu-hot-0"));

        var closed = 0;
        vm.RequestClose += () => closed++;

        vm.CancelCommand.Execute(null);

        store.TryGet("gpu-hot-0", out _).Should().BeFalse("Cancel must not apply the pending selection");
        closed.Should().Be(1);
    }

    // ─────────────────────────── ApplyLabel / OverwriteHint reflect selection ───────────────────────────

    [Fact]
    public void ApplyLabelAndOverwriteHint_ReflectSelection()
    {
        var (store, catalog) = BuildFixture();
        store.Set(MakeAlert("gpu-hot-0")); // already has an alert

        var vm = new CopyAlertDialogViewModel(MakeAlert("cpu-pkg-0"), catalog, store);

        vm.ApplyLabel.Should().Be(string.Format(LocalizationService.Instance["CopyAlert_Apply"], 0));
        vm.OverwriteHint.Should().BeNull("nothing is selected yet");
        vm.ApplyCommand.CanExecute(null).Should().BeFalse();

        var gpu = vm.Candidates.Single(c => c.Name == "gpu-hot-0");
        vm.SelectedCandidates.Add(gpu);

        vm.ApplyLabel.Should().Be(string.Format(LocalizationService.Instance["CopyAlert_Apply"], 1));
        vm.OverwriteHint.Should().Be(LocalizationService.Instance["CopyAlert_OverwriteHint"],
            "gpu-hot-0 already has a configured alert");
        vm.ApplyCommand.CanExecute(null).Should().BeTrue();

        vm.SelectedCandidates.Remove(gpu);

        vm.ApplyLabel.Should().Be(string.Format(LocalizationService.Instance["CopyAlert_Apply"], 0));
        vm.OverwriteHint.Should().BeNull();
        vm.ApplyCommand.CanExecute(null).Should().BeFalse();
    }

    // ─────────────────────────── XAML / code-behind source-scrape guards ───────────────────────────

    [Fact]
    public void SettingsViewCodeBehindWiresCopyRequestedToTheDialog()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "SettingsView.axaml.cs"));

        text.Should().Contain("_wiredAlertsSection.CopyRequested += OnAlertCopyRequested",
            "Copy to… must open CopyAlertDialog the same way EditRequested opens SetAlertDialog");
        text.Should().Contain("new CopyAlertDialog(",
            "OnAlertCopyRequested must actually construct the dialog");
    }

    [Fact]
    public void CopyAlertDialogHasExactlyOnePrimaryButton()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CopyAlertDialog.axaml"));

        var primaries = Regex.Matches(text,
            @"<(?:Button|ToggleButton|RepeatButton|DropDownButton|SplitButton)\b[^>]*?\bClasses=""[^""]*\bprimary\b[^""]*""");
        primaries.Count.Should().Be(1, "Apply is the dialog's one primary action; Cancel must stay tertiary/secondary");

        var cancel = Regex.Match(text, @"<Button\b[^>]*?Command=""\{Binding CancelCommand\}""[^>]*?Classes=""([^""]+)""");
        cancel.Success.Should().BeTrue();
        cancel.Groups[1].Value.Should().NotContain("primary");
    }

    private static readonly string[] NewKeys =
    [
        "CopyAlert_Title",
        "CopyAlert_Filter",
        "CopyAlert_ShowAll",
        "CopyAlert_Apply",
        "CopyAlert_OverwriteHint",
    ];

    [Fact]
    public void AllFiveCopyAlertKeysAreDefinedInAllNineResxFiles()
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
