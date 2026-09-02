using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Guards the aurora wallpaper layers of DashboardBackgroundControl against colour literals and
/// un-eased motion (RemEx-orn90).
/// </summary>
/// <remarks>
/// The three aurora RadialGradientBrushes used to end each gradient on a literal transparent
/// black (<c>#00000000</c>) with a literal mid stop, which darkened the glow falloff instead of
/// fading it and assumed a dark palette. They now end on the same <c>GlassBaseDark</c> resource
/// as the base rectangle beneath them, so the glow fades to nothing regardless of palette. This
/// test pins that every colour in the control comes from a DynamicResource, and that every
/// animation eases with the Material-appropriate SineEaseInOut rather than linear-by-omission.
/// </remarks>
public class DashboardBackgroundPaletteTests
{
    private static readonly Regex HexColorLiteral = new(@"^#[0-9A-Fa-f]{6,8}$");

    [Fact]
    public void NoColorOrFillAttribute_IsAHexLiteral()
    {
        var doc = LoadControl();

        var offenders = doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName is "Color" or "Fill")
            .Where(a => HexColorLiteral.IsMatch(a.Value))
            .Select(a => $"{a.Parent!.Name.LocalName}.{a.Name.LocalName}=\"{a.Value}\"")
            .ToList();

        offenders.Should().BeEmpty(
            "every colour in the wallpaper layers must derive from a palette resource, not a " +
            "hard-coded hex literal that ignores the active palette");
    }

    [Fact]
    public void EveryGradientStop_UsesADynamicResourceColor()
    {
        var doc = LoadControl();

        var stops = doc.Descendants(Av("GradientStop")).ToList();
        stops.Should().NotBeEmpty("the aurora layers author their colours via GradientStop");

        foreach (var stop in stops)
        {
            var color = stop.Attribute("Color");
            color.Should().NotBeNull();
            color!.Value.Should().StartWith("{DynamicResource ",
                $"GradientStop at offset {stop.Attribute("Offset")?.Value} must resolve its " +
                "colour from the palette so it re-skins with the seed and re-themes live");
        }
    }

    [Fact]
    public void EveryAnimation_EasesWithSineEaseInOut()
    {
        var doc = LoadControl();

        var animations = doc.Descendants(Av("Animation")).ToList();
        animations.Should().NotBeEmpty("the control's breathing/pulse layers are Animation elements");

        foreach (var animation in animations)
        {
            animation.Attribute("Easing").Should().NotBeNull(
                $"Animation at offset {animation.Attribute("Duration")?.Value} must declare an " +
                "Easing attribute; linear (un-eased) motion reads as mechanical");
            animation.Attribute("Easing")!.Value.Should().Be("SineEaseInOut",
                "linear (un-eased) motion reads as mechanical; Material's breathing/pulse motion " +
                "should ease in and out, and a named easing (never a cubic-bezier string) is " +
                "required because a bezier string is parsed at runtime and would crash the " +
                "control on load");
        }
    }

    private static XDocument LoadControl()
        => XDocument.Parse(File.ReadAllText(Path.Combine(
            RepoRoot(), "remex.desktop", "Controls", "DashboardBackgroundControl.axaml")));

    private static XName Av(string localName)
        => XName.Get(localName, "https://github.com/avaloniaui");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
