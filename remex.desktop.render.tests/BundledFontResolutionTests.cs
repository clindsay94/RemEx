using Avalonia.Headless.XUnit;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;

namespace Remex.Desktop.Render.Tests;

/// <summary>
/// The file-pattern Orbitron URI really resolves, in every bundled weight (sweep D11).
/// </summary>
/// <remarks>
/// The folder address ("…/Assets/Fonts#Orbitron") made Avalonia load all eight bundled .ttf files to
/// find four. "…/Assets/Fonts/Orbitron-*.ttf#Orbitron" loads only the Orbitron files, but a pattern
/// that silently matched nothing would fall back to the default font with no error anywhere, so this
/// asks the real font manager for each weight and checks it got Orbitron at that weight.
/// </remarks>
public sealed class BundledFontResolutionTests
{
    [AvaloniaTheory]
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(700)]
    [InlineData(900)]
    public void OrbitronResolvesEveryBundledWeightFromItsOwnFiles(int weight)
    {
        var family = new FontFamily(PageDisplayFontRule.DefaultSubtitleFont);
        var typeface = new Typeface(family, FontStyle.Normal, (FontWeight)weight);

        FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphTypeface).Should().BeTrue(
            "the pattern must match the bundled Orbitron files");
        // StartWith, not Be: the Medium and Black files name their legacy family "Orbitron Medium" /
        // "Orbitron Black" (the typographic family is Orbitron). What matters is that it is Orbitron,
        // not the default font a pattern matching nothing would fall back to.
        glyphTypeface!.FamilyName.Should().StartWith("Orbitron");
        ((int)glyphTypeface.Weight).Should().Be(weight, "each bundled weight is its own file, so none should be synthesised");
    }
}
