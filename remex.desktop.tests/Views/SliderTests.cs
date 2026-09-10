using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the sliders after RemEx-vryje.
/// </summary>
/// <remarks>
/// <para>
/// The defect this file exists for is a keyboard one and it is invisible to anyone using a mouse.
/// Avalonia's <c>RangeBase.SmallChange</c> defaults to <b>1</b>, which is fine for a 0–360 hue
/// slider and useless for a range narrower than that: the contrast slider spans −1 to 1, so an
/// arrow key moved it a full unit and left exactly three reachable positions. Glass opacity, window
/// opacity and UI scale were the same shape. Nothing throws; the control simply cannot be operated
/// from the keyboard at any useful resolution.
/// </para>
/// <para>
/// The second one is quieter still: <c>TickPlacement</c> defaults to <c>None</c>, so the three
/// sliders that declared a <c>TickFrequency</c> were computing tick positions and drawing nothing.
/// </para>
/// </remarks>
public class SliderTests
{
    [Fact]
    public void EverySliderCanBeMovedFromTheKeyboardAtAUsefulResolution()
    {
        // The rule: one arrow press must not cross more than a tenth of the range. That number is a
        // judgement, but the failure it catches is not — anything coarser means the keyboard reaches
        // fewer than ten positions on a control whose whole purpose is fine adjustment.
        var offenders = new List<string>();

        foreach (var (file, tag) in Sliders())
        {
            var min = Number(tag, "Minimum");
            var max = Number(tag, "Maximum");
            if (min is null || max is null)
            {
                continue;
            }

            var range = max.Value - min.Value;
            // Avalonia's default when the attribute is absent, which is the whole problem.
            var step = Number(tag, "SmallChange") ?? 1d;

            // A SNAPPING slider is judged differently, and it has to be: one arrow press should
            // move exactly one tick, however few ticks there are. Grid size snaps every 10 over a
            // range of 90 — nine positions, and a step of 10 is precisely right even though it is
            // larger than a tenth of the range. Judging it by the general rule would have demanded
            // a step that lands between ticks and gets snapped back, i.e. an arrow key that does
            // nothing.
            var snaps = Regex.IsMatch(tag, @"\bIsSnapToTickEnabled=""True""");
            var tick = Number(tag, "TickFrequency");

            if (snaps && tick is not null)
            {
                if (Math.Abs(step - tick.Value) > 1e-9)
                {
                    offenders.Add(
                        $"{Path.GetFileName(file)}: snaps every {tick.Value.ToString(CultureInfo.InvariantCulture)} "
                        + $"but the keyboard moves {step.ToString(CultureInfo.InvariantCulture)}");
                }

                continue;
            }

            if (step > range / 10d)
            {
                offenders.Add(
                    $"{Path.GetFileName(file)}: range {range.ToString(CultureInfo.InvariantCulture)}, "
                    + $"SmallChange {step.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        offenders.Should().BeEmpty(
            "SmallChange defaults to 1, so a slider with a range narrower than 10 is unusable from "
            + "the keyboard until it says otherwise");
    }

    [Fact]
    public void ASliderWithTicksActuallyDrawsThem()
    {
        // TickFrequency without TickPlacement is arithmetic with no output. Someone setting a
        // frequency has said "this control has discrete steps"; the ticks are how a user learns
        // that before dragging.
        var offenders = Sliders()
            .Where(s => Regex.IsMatch(s.Tag, @"\bTickFrequency="""))
            .Where(s => !Regex.IsMatch(s.Tag, @"\bTickPlacement="""))
            .Select(s => Path.GetFileName(s.File))
            .ToList();

        offenders.Should().BeEmpty(
            "TickPlacement defaults to None, so a declared TickFrequency renders nothing at all");
    }

    [Fact]
    public void EverySliderSaysWhatItAdjusts()
    {
        // A slider announces a number and nothing else. Nine of the thirteen had no accessible name
        // at all, so a screen reader user heard a value with no idea what it belonged to. Every one
        // of them had a localized label sitting next to it, which is what these reuse — no new
        // strings, and therefore no nine-file localization pass.
        var unnamed = Sliders()
            .Where(s => !s.Tag.Contains("AutomationProperties.Name", StringComparison.Ordinal))
            .Select(s => $"{Path.GetFileName(s.File)}: {Summarise(s.Tag)}")
            .ToList();

        unnamed.Should().BeEmpty("a slider announces a bare number unless it is named");
    }

    [Fact]
    public void NoViewPaintsASliderItself()
    {
        var offenders = Sliders()
            .Where(s => Regex.IsMatch(s.Tag, @"\b(Foreground|Background|BorderBrush)="))
            .Select(s => Path.GetFileName(s.File))
            .ToList();

        offenders.Should().BeEmpty("slider colour is one rule in App.axaml");
    }

    [Fact]
    public void TheSharedRuleGivesEverySliderItsValueBubble()
    {
        // ANTI-VACUITY. "No view paints a slider" is also satisfied by nobody styling sliders at
        // all, which would leave them on Material's plain thumb — no readout while dragging, which
        // is the bead's headline acceptance criterion. The bubble comes from the discrete THEME,
        // not from SliderAssist: 3.19.0's SliderAssist carries only ThicknessTick.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

        var style = Regex.Match(app, @"<Style Selector=""Slider"">.*?</Style>", RegexOptions.Singleline);

        style.Success.Should().BeTrue("App.axaml has to carry the shared Slider rule");
        style.Value.Should().Contain("{DynamicResource MaterialDiscreteSliderV2}",
            "the value bubble is the discrete slider theme's thumb; without the Theme setter a "
            + "dragged slider shows no value at all");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static double? Number(string tag, string attribute)
    {
        var match = Regex.Match(tag, $@"\b{attribute}=""([^""]+)""");
        return match.Success
               && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string Summarise(string tag)
    {
        var collapsed = Regex.Replace(tag, @"\s+", " ").Trim();
        return collapsed.Length > 110 ? collapsed[..110] + "…" : collapsed;
    }

    private static (string File, string Tag)[] Sliders()
    {
        var sliders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"<Slider\b[^>]*?/?>", RegexOptions.Singleline)
                .Select(match => (File: file, Tag: match.Value)))
            .ToArray();

        sliders.Should().NotBeEmpty(
            "if this finds nothing every assertion above is vacuous — the element or the scan moved");
        return sliders;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
