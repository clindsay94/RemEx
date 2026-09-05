using Avalonia.Media;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The other half of <see cref="AuroraColorsTests"/>. That file proves the generator produces two
/// different sets; this one proves the apply path actually PICKS the right one — that
/// <c>ThemeService</c> pushes the tone-90 set onto <c>AuroraPrimary</c> under Light and the tone-30
/// set under Dark, rather than hardcoding one branch. A generator that answers correctly while the
/// caller always passes <c>isLight: false</c> would satisfy every assertion in
/// <c>AuroraColorsTests</c> and still leave the mesh unreadable on a light surface, with no
/// exception and no log line — this assembly has no headless render, so nothing else would see it.
/// </summary>
/// <remarks>
/// <c>PostToUiThread</c> is redirected inline for the same reason
/// <c>HardwareAccentInjectionTests</c> does it: there is no <c>Avalonia.Headless</c> reference here,
/// so nothing pumps <c>Dispatcher.UIThread</c> and a posted apply would never run.
/// </remarks>
public class AuroraResourcePushTests
{
    private const string SeedHex = "#224466";
    private const string Variant = "TonalSpot";

    private static readonly Color Seed = Color.Parse(SeedHex);

    private static CustomizationSettings SettingsFor(string themeMode) => new()
    {
        AccentColor = SeedHex,
        SchemeVariant = Variant,
        ThemeMode = themeMode,
        ThemeContrast = 0.0,
    };

    private static Color AuroraPrimary(ThemeService theme, string themeMode)
    {
        theme.ApplyCustomization(SettingsFor(themeMode));
        return theme.GetOverrideResource("AuroraPrimary").Should().BeOfType<Color>().Subject;
    }

    [Fact]
    public void LightModePushesTheToneNinetySet_AndDarkModePushesToneThirty()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };

        var light = AuroraPrimary(theme, ThemeModes.Light);
        var dark = AuroraPrimary(theme, ThemeModes.Dark);

        light.Should().NotBe(dark,
            "flipping the theme must repaint the mesh; one hardcoded branch would leave both modes identical");

        light.Should().Be(DynamicColorGenerator.AuroraColors(Seed, Variant, isLight: true).Primary,
            "Light must land on the tone-90 set — the light-surface containers");
        dark.Should().Be(DynamicColorGenerator.AuroraColors(Seed, Variant, isLight: false).Primary,
            "Dark must land on the tone-30 set");
    }
}
