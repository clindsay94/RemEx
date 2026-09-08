using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-8wpvr.4: a bell on configured Canvas sensor cards, a red ringing bell when tripped, and a
/// smooth infinite pulse on live cards with a reduced-motion branch. No headless render exists for
/// this app (see the class remarks in <c>ButtonVocabularyTests</c>), so the visual wiring is guarded
/// by source-scraping <c>CanvasView.axaml</c> the same way that file already does; the localized
/// tooltip text is guarded behaviourally against <see cref="CanvasCardViewModel"/> directly.
/// </summary>
public class CanvasAlertVisualTests
{
    // ─────────────────────────── XAML source-scrape guards ───────────────────────────

    /// <summary>
    /// The header bell Button must be visible only when the card has an alert, must reflect the
    /// tripped state via the class binding the two Kind/Foreground styles switch on, and must run
    /// <c>AcknowledgeAlertCommand</c> — the same command <c>DraggableCard.OnPointerPressed</c> already
    /// runs for a whole-card tripped-click, so a stale/renamed command name here would silently break
    /// only the bell, not the card-body path (RemEx-8wpvr.2), and nothing else would catch it.
    /// </summary>
    [Fact]
    public void AlertBellBindsHasAlertIsAlertTrippedAndAcknowledgeCommand()
    {
        var bellTag = ExtractAlertBellButtonTag();

        bellTag.Should().Contain("IsVisible=\"{Binding HasAlert}\"",
            "the bell must only show on a card with a configured alert");
        bellTag.Should().Contain("Classes.tripped=\"{Binding IsAlertTripped}\"",
            "the tripped/armed Kind+Foreground swap is driven by a class bound to IsAlertTripped");
        bellTag.Should().Contain("Command=\"{Binding AcknowledgeAlertCommand}\"",
            "clicking the bell must acknowledge the trip the same way clicking the card body does");
    }

    /// <summary>The pulse must actually loop — a finite <c>IterationCount</c> would play once and stop
    /// looking identical to the old flash it replaced.</summary>
    [Fact]
    public void AlertActivePulseAnimationRunsForever()
    {
        var animatedStyle = ExtractStyleBlock("ctrl|DraggableCard.alert-active:not(.reduced-motion)");
        animatedStyle.Should().Contain("IterationCount=\"Infinite\"",
            "the live pulse must loop for as long as the card is hot, not play once");
        animatedStyle.Should().Contain("<Style.Animations>",
            "the animated variant is the one that actually carries the keyframe animation");
    }

    /// <summary>The reduced-motion variant must hold a steady glow instead of animating it — a
    /// Style.Animations here would defeat the whole point of the branch (RemEx-8wpvr.4 design doc,
    /// "Canvas visuals").</summary>
    [Fact]
    public void ReducedMotionVariantHasNoAnimation()
    {
        var reducedStyle = ExtractStyleBlock("ctrl|DraggableCard.alert-active.reduced-motion");
        reducedStyle.Should().NotContain("Style.Animations",
            "reduced motion must hold a steady glow, not a slowed-down or hidden animation");
        reducedStyle.Should().NotContain("<Animation ",
            "reduced motion must hold a steady glow, not a slowed-down or hidden animation");
        reducedStyle.Should().Contain("Effect",
            "the reduced-motion card must still show the glow, just without motion");
    }

    // ─────────────────────────── localization ───────────────────────────

    private static readonly string[] NewKeys = ["Canvas_AlertBellConfigured", "Canvas_AlertBellTripped"];

    [Fact]
    public void BothAlertBellKeysAreDefinedInAllNineResxFiles()
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

    // ─────────────────────────── AlertTooltip behaviour ───────────────────────────

    private static CanvasDashboardViewModel NewDashboard() =>
        new(new ConnectionViewModel(), null!, null!, new SensorAlertStore(), new SensorAlertTracker());

    private static TelemetryPayload Reading(string id, double value) => new()
    {
        Sensors = new List<SensorReading> { new() { Id = id, Name = id, Value = value, Unit = "°C" } },
    };

    /// <summary>
    /// Proves the configured-state tooltip actually comes from <c>Canvas_AlertBellConfigured</c>
    /// rather than a hardcoded English sentence: switching the active culture to French must change
    /// the rendered text, which a literal string never would.
    /// </summary>
    [Fact]
    public void AlertTooltip_Configured_IsBuiltFromTheLocalizedResource()
    {
        WithCulture("fr", () =>
        {
            var vm = NewDashboard();
            vm.ApplyTelemetry(Reading("cpu-pkg-0", 10));
            var card = vm.StagedCards.Single();
            card.Sensor!.Alert = new SensorAlert
            {
                SensorName = "cpu-pkg-0", Threshold = 90, Direction = AlertDirection.Above, Severity = AlertSeverity.Critical,
            };

            var direction = LocalizationService.Instance["AlertDirection_Above"];
            var severity = LocalizationService.Instance["AlertSeverity_Critical"];
            var threshold = SensorReadingFormat.FormatReading(90, card.Sensor.Unit);
            var expected = string.Format(
                LocalizationService.Instance["Canvas_AlertBellConfigured"], direction, threshold, severity);

            card.AlertTooltip.Should().Be(expected);
            card.AlertTooltip.Should().NotBe("Alert: Above 90 °C · Critical",
                "the tooltip must not be the pre-.4 hardcoded English sentence once the culture is French");
        });
    }

    /// <summary>Same proof for the tripped-state tooltip, built from <c>Canvas_AlertBellTripped</c>.</summary>
    [Fact]
    public void AlertTooltip_Tripped_IsBuiltFromTheLocalizedResource()
    {
        WithCulture("fr", () =>
        {
            var vm = NewDashboard();
            vm.ApplyTelemetry(Reading("gpu-hot-0", 10));
            var card = vm.StagedCards.Single();
            var alert = new SensorAlert
            {
                SensorName = "gpu-hot-0", Threshold = 90, Direction = AlertDirection.Above, Severity = AlertSeverity.Warning,
            };
            card.Sensor!.Alert = alert;

            var at = new DateTimeOffset(2026, 1, 1, 8, 30, 0, TimeSpan.Zero);
            card.IsAlertTripped = true;
            card.SetTrippedAlert(new TrippedAlert("gpu-hot-0", at, 91.2, alert));

            var localTime = at.LocalDateTime.ToString("t", LocalizationService.Instance.Culture);
            var trippedValue = SensorReadingFormat.FormatReading(91.2, card.Sensor.Unit);
            var expected = string.Format(
                LocalizationService.Instance["Canvas_AlertBellTripped"], localTime, trippedValue);

            card.AlertTooltip.Should().Be(expected);
            card.AlertTooltip.Should().NotContain("Click to acknowledge.",
                "the French tooltip must not fall back to the pre-.4 hardcoded English sentence");
        });
    }

    private static void WithCulture(string culture, Action assert)
    {
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture(culture);
            assert();
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string CanvasViewText() =>
        StripXmlComments(File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml")));

    private static string StripXmlComments(string xaml) =>
        Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /// <summary>Pulls the full opening tag of the header alert-bell Button (identified by its
    /// distinctive "alert-bell" vocabulary exception class) out of the sensor card template.</summary>
    private static string ExtractAlertBellButtonTag()
    {
        var text = CanvasViewText();
        var match = Regex.Match(text, @"<Button\b[^>]*\balert-bell\b[^>]*>", RegexOptions.Singleline);
        match.Success.Should().BeTrue("CanvasView.axaml must declare the header alert bell Button");
        return match.Value;
    }

    /// <summary>Extracts one <c>&lt;Style Selector="..."&gt;...&lt;/Style&gt;</c> block by its exact
    /// selector text, so the pulse/reduced-motion assertions read only their own style and cannot be
    /// satisfied by content that belongs to the other one.</summary>
    private static string ExtractStyleBlock(string selector)
    {
        var text = CanvasViewText();
        var pattern = $@"<Style\s+Selector=""{Regex.Escape(selector)}"".*?</Style>";
        var match = Regex.Match(text, pattern, RegexOptions.Singleline);
        match.Success.Should().BeTrue($"CanvasView.axaml must declare a Style with Selector=\"{selector}\"");
        return match.Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
