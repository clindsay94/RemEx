using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins that every theme supplies a readable foreground for accent-filled surfaces (RemEx-tq2e).
/// </summary>
/// <remarks>
/// <para>
/// Nine call sites used a literal <c>Foreground="White"</c> on an accent-filled button. Measured
/// against each theme's own accent, that literal FAILS WCAG AA on three of the four — CyberNOC at
/// 1.38:1 is barely legible, SolarFlare 1.73:1, Monolith 3.65:1 — and passes only on BaseDarkGlass.
/// </para>
/// <para>
/// **THE VALUES ARE ARITHMETIC, NOT TASTE**, which is what let this be fixed rather than deferred:
/// the correct foreground is simply whichever of black or white contrasts better, and it differs per
/// theme. That difference is also why a single literal could never have worked.
/// </para>
/// <para>
/// A MISSING DynamicResource IS SILENT IN AVALONIA — it is neither a compile error nor a runtime
/// one, so a theme that dropped this key would render an unstyled default and nothing would say so.
/// This reads the theme files directly because that is the only way to catch it.
/// </para>
/// </remarks>
public class AccentForegroundContrastTests
{
    private static readonly string[] Themes = ["CyberNOC", "Monolith", "SolarFlare", "BaseDarkGlass"];

    private static string ThemePath(string theme) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Themes", theme + ".axaml");

    private static double RelativeLuminance(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];

        var channels = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var v = Convert.ToInt32(h.Substring(i * 2, 2), 16) / 255.0;
            channels[i] = v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
    }

    private static double Contrast(string a, string b)
    {
        var (x, y) = (RelativeLuminance(a), RelativeLuminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [Fact]
    public void EveryThemeDeclaresAnAccentForeground()
    {
        // A DynamicResource that resolves to nothing is silent in Avalonia, so a theme that dropped
        // the key would render an unstyled default with no error anywhere.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));
            Assert.Contains("AccentForegroundBrush", text);
        }
    }

    [Fact]
    public void TheAccentForegroundMeetsWcagAAAgainstItsOwnAccent()
    {
        // THE PROPERTY, CHECKED RATHER THAN ASSERTED FROM A TABLE. If a future theme changes its
        // accent, this fails until its foreground is revisited - which is the whole reason the value
        // is per-theme rather than one literal.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));

            var accent = Extract(text, "<Color x:Key=\"AccentPrimary\">");
            var foreground = ExtractBrush(text, "AccentForegroundBrush");

            var ratio = Contrast(accent, foreground);
            Assert.True(ratio >= 4.5,
                $"{theme}: foreground {foreground} on accent {accent} is {ratio:F2}:1, below WCAG AA 4.5:1");
        }
    }

    [Fact]
    public void PlainWhiteWouldFailOnMostThemes_WhichIsWhyTheTokenExists()
    {
        // The control. Without it the test above could pass against a token that merely happens to
        // equal the literal it replaced, and the change would look justified when it was not.
        var failures = 0;
        foreach (var theme in Themes)
        {
            var accent = Extract(File.ReadAllText(ThemePath(theme)), "<Color x:Key=\"AccentPrimary\">");
            if (Contrast(accent, "#FFFFFF") < 4.5) failures++;
        }

        Assert.Equal(3, failures);
    }

    private static string Extract(string text, string marker)
    {
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"missing {marker}");
        var start = text.IndexOf('#', i);
        return text.Substring(start, 7);
    }

    private static string ExtractBrush(string text, string key)
    {
        var i = text.IndexOf(key, StringComparison.Ordinal);
        Assert.True(i >= 0, $"missing {key}");
        var start = text.IndexOf('#', i);
        return text.Substring(start, 9);
    }
}
