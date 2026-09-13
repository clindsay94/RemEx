using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards RemEx-1ufoa.2, carried into Spec B's family styling (RemEx-4kv0g.3.2). The dual-metric
/// legend plate on the Sensors card is, for an overridden card, the per-sensor
/// <c>Theme.CardBackground</c> (dark by default) at translucent alpha — so an overridden
/// card's legend/value/title text must still bind through its OWN <c>Theme.*</c>, never a
/// generic palette brush that could be dark-on-dark on a light seed. Each family's themed text must
/// use that family's own paired ink token, never a different family's or a hardcoded literal.
/// </summary>
/// <remarks>
/// FIX ROUND 1 RESTORES THE RIGOUR an earlier pass replaced with <c>string.Contain()</c> over the
/// whole style file — a substring check passes whether a setter sits under the right selector or
/// the wrong one, and whether a TextBlock in the card content still carries its class or lost it.
/// This parses <c>Styles/SensorCardFamilies.axaml</c> as XML and resolves each assertion against
/// the specific Style element the Selector attribute names (exact selector-part matching, not
/// substring — "TextBlock.card-value" is itself a substring of "TextBlock.card-value-2", which is
/// exactly the kind of false-positive substring matching invites), and parses
/// <c>Controls/SensorCardContent.axaml</c> (RemEx-4kv0g.18.1 moved the card content there out of
/// CanvasView.axaml) to confirm the legend TextBlocks still carry the class the style rules depend
/// on.
/// </remarks>
public class CanvasLegendContrastTests
{
    private static readonly string[] Families = { "primary", "secondary", "tertiary", "neutral" };

    [Theory]
    [MemberData(nameof(FamilyNames))]
    public void EachFamilysUnitAndLegendNameStyle_SetsThatFamilysInkDimBrush(string family)
    {
        var doc = LoadStyles();
        var capitalized = char.ToUpperInvariant(family[0]) + family[1..];

        var style = FindStyleByExactSelectors(doc,
            $".family-{family} TextBlock.card-unit",
            $".family-{family} TextBlock.card-legend-name");

        SetterValue(style, "Foreground").Should().Be($"{{DynamicResource CardInkDim{capitalized}Brush}}",
            $"family-{family}'s unit and legend-name text must both come from the SAME setter using " +
            $"CardInkDim{capitalized}Brush — a setter under the wrong family selector would still " +
            "satisfy a substring Contain() check but fail this exact-selector lookup");
    }

    [Fact]
    public void OverriddenCard_UnitAndLegendNameStyle_BindsThemeUnitColor()
    {
        // RemEx-4kv0g.18.1: the canvas-only "ctrl|DraggableCard.family-custom" selector is gone —
        // SensorCardContent's DataContext is the SensorViewModel on every host, so one
        // host-agnostic ".family-custom" selector now binds Theme.UnitColor directly (no "Sensor."
        // prefix) for canvas, Home tile and tray strip alike.
        var doc = LoadStyles();

        var style = FindStyleByExactSelectors(doc,
            ".family-custom TextBlock.card-unit",
            ".family-custom TextBlock.card-legend-name");

        SetterValue(style, "Foreground").Should().Be(
            "{Binding Theme.UnitColor, Converter={x:Static conv:HexToBrushConverter.Instance}}",
            "an overridden card's legend names and unit text must bind Theme.UnitColor, never a palette brush");
    }

    [Fact]
    public void OverriddenCard_TitleAndValueStyles_EachBindTheirOwnThemeField()
    {
        var doc = LoadStyles();

        SetterValue(FindStyleByExactSelectors(doc, ".family-custom TextBlock.sensor-title"), "Foreground")
            .Should().Be("{Binding Theme.LabelColor, Converter={x:Static conv:HexToBrushConverter.Instance}}",
                "the title must bind LabelColor specifically, not a neighbouring field");

        SetterValue(FindStyleByExactSelectors(doc, ".family-custom TextBlock.card-value"), "Foreground")
            .Should().Be("{Binding Theme.ValueColor, Converter={x:Static conv:HexToBrushConverter.Instance}}",
                "the primary value must bind ValueColor, not the LabelColor a substring check could not tell apart");

        SetterValue(FindStyleByExactSelectors(doc, ".family-custom TextBlock.card-value-2"), "Foreground")
            .Should().Be("{Binding SecondaryAccentHex, Converter={x:Static conv:HexToBrushConverter.Instance}}",
                "the secondary (dual-metric) value must follow the sibling sensor's accent, not the primary ValueColor");
    }

    [Fact]
    public void SensorCardContent_BothLegendNameTextBlocks_CarryTheLegendNameClass()
    {
        // RemEx-4kv0g.18.1: this structure lives in Controls/SensorCardContent.axaml now, and the
        // legend Border's binding dropped its "Sensor." prefix along with everything else it moved.
        var doc = XDocument.Parse(ReadSensorCardContent());
        var ns = doc.Root!.GetDefaultNamespace();

        var legendBorder = doc.Descendants(ns + "Border")
            .SingleOrDefault(b => (string?)b.Attribute("IsVisible") == "{Binding IsDualMetric}");

        legendBorder.Should().NotBeNull("exactly one dual-metric legend Border should exist on the Sensors card");

        var legendNameTextBlocks = legendBorder!.Descendants(ns + "TextBlock")
            .Where(tb => (string?)tb.Attribute("Classes") == "card-legend-name")
            .ToArray();

        legendNameTextBlocks.Should().HaveCount(2,
            "both the primary and secondary metric name labels must carry Classes=\"card-legend-name\" — " +
            "losing the class on either one silently leaves that label unthemed, which no style-file " +
            "assertion alone can catch");
    }

    private static XDocument LoadStyles() => XDocument.Parse(ReadSensorCardFamilies());

    /// <summary>
    /// Finds the Style element whose comma-split Selector parts are EXACTLY the given set (same
    /// count, same members) — not a substring match, which would conflate "TextBlock.card-value"
    /// with "TextBlock.card-value-2". Fails loudly (not silently returns null) when zero or more
    /// than one Style matches, since either means the file no longer has the shape this test needs.
    /// </summary>
    private static XElement FindStyleByExactSelectors(XDocument doc, params string[] exactSelectorParts)
    {
        var ns = doc.Root!.GetDefaultNamespace();
        var matches = doc.Root!.Elements(ns + "Style")
            .Where(s =>
            {
                var parts = ((string?)s.Attribute("Selector") ?? string.Empty)
                    .Split(',').Select(p => p.Trim()).ToArray();
                return parts.Length == exactSelectorParts.Length && exactSelectorParts.All(parts.Contains);
            })
            .ToArray();

        matches.Should().HaveCount(1,
            $"expected exactly one Style with selector parts [{string.Join(" | ", exactSelectorParts)}] " +
            "in SensorCardFamilies.axaml");
        return matches[0];
    }

    private static string SetterValue(XElement style, string property)
    {
        var ns = style.GetDefaultNamespace();
        var setter = style.Elements(ns + "Setter")
            .SingleOrDefault(s => (string?)s.Attribute("Property") == property);

        setter.Should().NotBeNull(
            $"Style '{(string?)style.Attribute("Selector")}' must carry a Setter for {property}");
        return (string)setter!.Attribute("Value")!;
    }

    public static TheoryData<string> FamilyNames()
    {
        var data = new TheoryData<string>();
        foreach (var family in Families)
            data.Add(family);
        return data;
    }

    private static string ReadSensorCardFamilies()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Styles", "SensorCardFamilies.axaml"));

    private static string ReadSensorCardContent()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "SensorCardContent.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
