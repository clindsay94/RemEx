using System.Linq;
using System.Xml.Linq;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins <see cref="PaletteExchange"/>'s JSON round-trip and AXAML shape (RemEx-a7uzb). Pure static
/// service — no Avalonia runtime needed, same reason <c>SeedHctTests</c> can run headless.
/// </summary>
public class PaletteExchangeTests
{
    private static PaletteRecipe SampleRecipe() =>
        new("#FF00F3FF", "Vibrant", ThemeModes_Dark, -0.35, 42.5);

    // Local copy so this file does not need Remex.Core.Models just for one constant string.
    private const string ThemeModes_Dark = "Dark";

    [Fact]
    public void ToJson_TryParseJson_RoundTripsEveryField()
    {
        var recipe = SampleRecipe();

        var json = PaletteExchange.ToJson(recipe);
        var ok = PaletteExchange.TryParseJson(json, out var parsed);

        ok.Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed!.Seed.Should().Be(recipe.Seed);
        parsed.Variant.Should().Be(recipe.Variant);
        parsed.Mode.Should().Be(recipe.Mode);
        parsed.Contrast.Should().Be(recipe.Contrast);
        parsed.SeedChroma.Should().Be(recipe.SeedChroma);
    }

    [Fact]
    public void ToJson_TryParseJson_RoundTripsNineCharSeed()
    {
        var recipe = new PaletteRecipe("#80FF00F3", "TonalSpot", "Light", 1.0, 12.0);

        var json = PaletteExchange.ToJson(recipe);
        var ok = PaletteExchange.TryParseJson(json, out var parsed);

        ok.Should().BeTrue();
        parsed!.Seed.Should().Be("#80FF00F3");
    }

    [Fact]
    public void ToJson_EmitsCamelCaseAndFormatVersion()
    {
        var json = PaletteExchange.ToJson(SampleRecipe());

        json.Should().Contain("\"formatVersion\"");
        json.Should().Contain("\"seed\"");
        json.Should().Contain("\"seedChroma\"");
    }

    [Fact]
    public void TryParseJson_RejectsGarbage()
    {
        PaletteExchange.TryParseJson("not json at all {{{", out var recipe).Should().BeFalse();
        recipe.Should().BeNull();
    }

    [Fact]
    public void TryParseJson_RejectsUnknownVariant()
    {
        var json = PaletteExchange.ToJson(SampleRecipe()).Replace("Vibrant", "Psychedelic");
        PaletteExchange.TryParseJson(json, out var recipe).Should().BeFalse();
        recipe.Should().BeNull();
    }

    [Fact]
    public void TryParseJson_RejectsBadMode()
    {
        var json = PaletteExchange.ToJson(SampleRecipe()).Replace("Dark", "Twilight");
        PaletteExchange.TryParseJson(json, out var recipe).Should().BeFalse();
        recipe.Should().BeNull();
    }

    [Fact]
    public void TryParseJson_RejectsInvalidHexSeed()
    {
        var json = PaletteExchange.ToJson(SampleRecipe()).Replace("#FF00F3FF", "#FF0O00FF");
        PaletteExchange.TryParseJson(json, out var recipe).Should().BeFalse();
        recipe.Should().BeNull();
    }

    private static DynamicColorGenerator.M3Palette SamplePalette() =>
        DynamicColorGenerator.Generate(Color.Parse("#FF00F3FF"), "Vibrant", isDark: true, contrast: 0.0);

    [Fact]
    public void ToAxaml_IsWellFormedResourceDictionaryWith28ColorsAnd28Brushes()
    {
        var xml = PaletteExchange.ToAxaml(SamplePalette(), SampleRecipe());

        var doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("ResourceDictionary");

        var colors = doc.Root.Elements().Where(e => e.Name.LocalName == "Color").ToList();
        var brushes = doc.Root.Elements().Where(e => e.Name.LocalName == "SolidColorBrush").ToList();

        colors.Should().HaveCount(28);
        brushes.Should().HaveCount(28);
        colors.Should().NotBeEmpty();
        brushes.Should().NotBeEmpty();
    }

    [Fact]
    public void ToAxaml_EveryColorValueIsAARRGGBBHex()
    {
        var xml = PaletteExchange.ToAxaml(SamplePalette(), SampleRecipe());
        var doc = XDocument.Parse(xml);

        var colors = doc.Root!.Elements().Where(e => e.Name.LocalName == "Color");
        foreach (var color in colors)
        {
            color.Value.Should().MatchRegex("^#[0-9A-F]{8}$");
        }
    }

    [Fact]
    public void ToAxaml_EveryBrushReferencesAnEmittedColorKey()
    {
        var xNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var xml = PaletteExchange.ToAxaml(SamplePalette(), SampleRecipe());
        var doc = XDocument.Parse(xml);

        var colorKeys = doc.Root!.Elements()
            .Where(e => e.Name.LocalName == "Color")
            .Select(e => e.Attribute(xNs + "Key")!.Value)
            .ToHashSet();

        var brushes = doc.Root.Elements().Where(e => e.Name.LocalName == "SolidColorBrush").ToList();
        brushes.Should().NotBeEmpty();

        foreach (var brush in brushes)
        {
            var colorAttr = brush.Attribute("Color")!.Value;
            colorAttr.Should().StartWith("{StaticResource ").And.EndWith("}");
            var referencedKey = colorAttr.Substring("{StaticResource ".Length).TrimEnd('}');
            colorKeys.Should().Contain(referencedKey);
        }
    }
}
