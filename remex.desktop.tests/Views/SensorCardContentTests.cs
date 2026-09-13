using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the RemEx-4kv0g.18.1 extraction: the sensor card's visual moved out of
/// <c>Views/CanvasView.axaml</c>'s DataTemplate into <c>Controls/SensorCardContent.axaml</c>
/// verbatim, so the canvas can host it and the tray flyout (.18.2) can host the same control.
/// Source scan, not a rendering test — there is no headless Avalonia harness here (see
/// <see cref="TrayFlyoutSurfaceTests"/>, which this follows the shape of).
/// </summary>
public class SensorCardContentTests
{
    [Fact]
    public void CanvasView_ReferencesSensorCardContent_AndNoLongerHoldsTheCardMarkupItself()
    {
        var canvas = Read("Views", "CanvasView.axaml");

        canvas.Should().Contain("<ctrl:SensorCardContent",
            "CanvasView.axaml must host the sensor card's visual through the extracted control");

        foreach (var goneFromCanvas in new[] { "card-plate", "card-legend-1", "card-legend-plate", "card-value-2", "ctrl:SparklineControl" })
        {
            canvas.Should().NotContain(goneFromCanvas,
                $"'{goneFromCanvas}' belonged to the card content, which moved to Controls/SensorCardContent.axaml — " +
                "it surviving in CanvasView.axaml would mean the extraction left a duplicate behind");
        }
    }

    [Fact]
    public void SensorCardContent_CarriesEveryPartClassFromTheSpecBInterface()
    {
        var content = Read("Controls", "SensorCardContent.axaml");

        // The exact part-class list from .superpowers/sdd/2026-09-12-sensor-cards/interface.md
        // ("Style classes (Styles/SensorCardFamilies.axaml)") — every one of these must still exist
        // on the extracted control, or a family/family-custom rule in SensorCardFamilies.axaml
        // would silently stop matching anything.
        foreach (var partClass in new[]
        {
            "sensor-title", "card-value", "card-value-2", "card-unit", "card-pill", "card-plate",
            "card-legend-plate", "card-spark", "card-legend-1", "card-legend-2", "card-legend-name",
            "card-icon",
        })
        {
            content.Should().Contain(partClass,
                $"SensorCardContent.axaml must still carry the '{partClass}' part class from the spec B interface");
        }
    }

    [Fact]
    public void SensorCardContent_HasNoInlineColourOnAClassedPart()
    {
        // Mirrors SensorCardBindingsTests from the other direction, scoped to the one file that
        // test doesn't scan for these specific attribute names: a local Foreground/Background/
        // AccentColor/SecondaryAccentColor always outranks the family/family-custom Style setters
        // in SensorCardFamilies.axaml regardless of selector specificity (see that file's header
        // comment), so any of these appearing here at all would silently break per-family
        // repainting for whichever part carries it.
        var content = Read("Controls", "SensorCardContent.axaml");

        foreach (var pattern in new[] { "Foreground=", "Background=", "AccentColor=", "SecondaryAccentColor=" })
        {
            content.Should().NotContain(pattern,
                $"SensorCardContent.axaml must not set {pattern} inline — that colour belongs in " +
                "Styles/SensorCardFamilies.axaml, scoped under a family-* or family-custom selector");
        }
    }

    [Fact]
    public void NoInlineFontSizeSurvives()
    {
        // Same rule TrayFlyoutSurfaceTests.NoInlineFontSizeSurvives enforces there: every text part
        // should sit on a type-scale Theme, not an inline size.
        var content = Read("Controls", "SensorCardContent.axaml");
        content.Should().NotContain("FontSize=",
            "every text part in SensorCardContent.axaml should be on a type-scale Theme, not an inline size");
    }

    /// <summary>
    /// Fix round 1 (Opus review, MEDIUM 2): pins the host's six bindings so a future edit can't
    /// silently reintroduce the "Sensor." prefix (which would break every one of them, since the
    /// card VM has no such property) or a DataContext="{Binding Sensor}" attribute on the element
    /// (the exact trap the brief named — it would flip the OTHER five bindings on this same
    /// element to resolve against the sensor instead of the card VM).
    /// </summary>
    [Fact]
    public void CanvasView_SensorCardContentElement_BindsAllSixHostPropertiesWithNoPrefixAndNoDataContext()
    {
        var doc = XDocument.Parse(Read("Views", "CanvasView.axaml"));
        const string ctrlNs = "using:Remex.Desktop.Controls";
        var element = doc.Descendants(XName.Get("SensorCardContent", ctrlNs)).SingleOrDefault();

        element.Should().NotBeNull("CanvasView.axaml must host the sensor card via exactly one ctrl:SensorCardContent element");

        var expectedBindings = new Dictionary<string, string>
        {
            ["Sensor"] = "{Binding Sensor}",
            ["IsPinnedToHome"] = "{Binding IsPinnedToHome}",
            ["HasAlert"] = "{Binding HasAlert}",
            ["IsAlertTripped"] = "{Binding IsAlertTripped}",
            ["AcknowledgeAlertCommand"] = "{Binding AcknowledgeAlertCommand}",
            ["AlertTooltip"] = "{Binding AlertTooltip}",
        };

        foreach (var (attribute, expectedValue) in expectedBindings)
        {
            ((string?)element!.Attribute(attribute)).Should().Be(expectedValue,
                $"the host must bind {attribute} against the card VM directly — no \"Sensor.\" prefix, " +
                "since the card VM (not the sensor) is this element's DataContext");
        }

        element!.Attribute("DataContext").Should().BeNull(
            "a DataContext=\"{Binding Sensor}\" attribute here would flip the five properties above to " +
            "resolve against the sensor instead of the card VM — Sensor is a styled property specifically " +
            "so the host never needs to set DataContext on this element at all");
    }

    /// <summary>
    /// Fix round 1 (Opus review, HIGH): the rename TextBox's own IsVisible only gates on
    /// Sensor.IsEditingTitle, which is null (not false) for the Connection/Actions/Latency cards —
    /// IsVisible then falls back to its default (true). It must ALSO sit inside a container gated
    /// on CardType being a sensor, so a null Sensor never leaves a stray, click-eating TextBox over
    /// the other three card types.
    /// </summary>
    [Fact]
    public void CanvasView_RenameTextBox_SitsUnderTheIsSensorGatedContainer()
    {
        var doc = XDocument.Parse(Read("Views", "CanvasView.axaml"));
        var ns = doc.Root!.GetDefaultNamespace();

        var renameBox = doc.Descendants(ns + "TextBox")
            .SingleOrDefault(tb => (string?)tb.Attribute("Loaded") == "OnRenameBoxLoaded");

        renameBox.Should().NotBeNull("the inline rename editor must still exist in CanvasView.axaml");

        var gatedAncestor = renameBox!.Ancestors()
            .FirstOrDefault(a => ((string?)a.Attribute("IsVisible"))?.Contains("StringMatchConverter.IsSensor") == true);

        gatedAncestor.Should().NotBeNull(
            "the rename TextBox must sit inside a container whose IsVisible is gated on CardType being a " +
            "sensor (the same binding the SensorCardContent host uses) — its own Sensor.IsEditingTitle " +
            "binding alone defaults to visible when Sensor is null, on the other three card types");
    }

    private static string Read(string folder, string file) => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", folder, file));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
