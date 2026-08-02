using System.Text.RegularExpressions;
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
    private static string ThemesDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Themes");

    /// <summary>Every theme on disk, not a list someone has to remember to extend.</summary>
    /// <remarks>
    /// A HARDCODED LIST IS SILENTLY INCOMPLETE. Adding a fifth theme without the tokens would give
    /// exactly the unresolved-DynamicResource failure this class exists to catch, with every test
    /// green, because the new file would never be looked at.
    /// </remarks>
    private static string[] Themes =>
        Directory.EnumerateFiles(ThemesDirectory(), "*.axaml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

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

        // EXPRESSED AGAINST THE LIST, NOT AS 3. Themes now comes from disk, so a literal 3 would
        // fail on a fifth theme for the wrong reason - it would look like a contrast regression when
        // it is only a bigger list. White passes on exactly one theme, BaseDarkGlass.
        Assert.Equal(Themes.Length - 1, failures);
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
        // MATCHED ON THE DECLARATION, NOT THE BARE NAME. IndexOf(key) finds the first MENTION, and
        // the rationale comment above SuccessForegroundBrush names AccentForegroundBrush - so this
        // resolved correctly only because the accent declaration happens to come first in all four
        // files. A reorder would have silently made the accent test measure the green value and pass.
        var i = text.IndexOf($"x:Key=\"{key}\"", StringComparison.Ordinal);
        Assert.True(i >= 0, $"missing {key}");
        var start = text.IndexOf('#', i);
        return text.Substring(start, 9);
    }

    // ── The success surface, and the views themselves (RemEx-iegl) ─────────────────────────────

    [Fact]
    public void EveryThemeDeclaresASuccessForeground()
    {
        foreach (var theme in Themes)
        {
            Assert.Contains("SuccessForegroundBrush", File.ReadAllText(ThemePath(theme)));
        }
    }

    [Fact]
    public void TheSuccessForegroundMeetsWcagAAAgainstItsOwnSuccess()
    {
        // A SEPARATE TOKEN, NOT A REUSE OF THE ACCENT ONE, and measurement is why. White fails on
        // the success fill in ALL FOUR themes (2.28, 1.34, 2.02, 3.30), and AccentForegroundBrush
        // does not rescue it either: on BaseDarkGlass that token IS white, correctly, because its
        // purple accent wants white - so borrowing it would leave the green button at 2.28 while
        // looking fixed. Green needs dark text in every theme; the accent does not.
        foreach (var theme in Themes)
        {
            var text = File.ReadAllText(ThemePath(theme));
            var success = Extract(text, "<Color x:Key=\"SystemSuccess\">");
            var foreground = ExtractBrush(text, "SuccessForegroundBrush");

            var ratio = Contrast(success, foreground);
            Assert.True(ratio >= 4.5, $"{theme}: {foreground} on {success} is {ratio:F2}:1, below AA.");
        }
    }

    /// <summary>Every white-on-filled offence in one .axaml, in both spellings that occur here.</summary>
    /// <remarks>
    /// <para>
    /// **SHARED WITH THE ANTI-VACUITY TEST ON PURPOSE.** The first version of that test built a
    /// synthetic offender and re-implemented the matching beside it, so it proved <c>String.Split</c>
    /// worked and nothing about the scan — it would have stayed green through a wrong path, an empty
    /// brush list, or drift between the two copies. Probing the real function is the only version
    /// that means anything.
    /// </para>
    /// <para>
    /// **TWO SPELLINGS, BECAUSE THE FIRST VERSION ONLY SAW ONE.** Splitting on '&lt;' finds the
    /// inline form, where one element carries both attributes. It is structurally blind to the style
    /// form, where <c>&lt;Setter Property="Background"&gt;</c> and <c>&lt;Setter
    /// Property="Foreground"&gt;</c> land in different fragments — and review found SEVEN live
    /// buttons hiding in exactly that gap while the scan reported zero.
    /// </para>
    /// <para>
    /// <c>Fill</c> counts as well as <c>Foreground</c>, because a glyph inside an accent button is a
    /// <c>Path</c> that <c>Foreground</c> never reaches, and WCAG 1.4.11 still wants 3:1 for it. The
    /// resource key is matched with an optional <c>Brush</c> suffix because this repo writes both —
    /// <c>CanvasView</c> binds <c>{DynamicResource SystemError}</c>, the Color key, directly.
    /// </para>
    /// <para>
    /// **WHAT THIS STILL CANNOT SEE, STATED RATHER THAN IMPLIED (RemEx-o9gd).** A glyph whose
    /// <c>Fill</c> is inline on a child element while its container is filled by a CLASS is invisible
    /// here: the two live on different elements, and neither the split nor the style pass brings them
    /// together. <c>ShellView</c>'s gear button was exactly that shape and is fixed in this change,
    /// but by hand — reverting it leaves this scan green, which was measured, not assumed. Closing it
    /// needs the class-to-ancestor resolution a real XAML parse would give.
    /// </para>
    /// <para>
    /// ERROR RED IS DELIBERATELY NOT LISTED. Measurement during RemEx-tq2e showed this very token
    /// making SolarFlare WORSE on red — 4.83:1 down to 3.81:1 — so those surfaces keep white until
    /// red gets its own measured token (RemEx-xb3c). White on red is below AA on three themes today; that is
    /// a separate bead, not something this rule can fix.
    /// </para>
    /// </remarks>
    private static List<string> ScanForWhiteOnFilled(string axaml, string label)
    {
        // LEFT-ANCHORED so an attribute merely ENDING in Foreground - SelectionForeground, say -
        // is not reported. None exist today; the guard should not create a false positive for the
        // first one that does.
        const string white = "\"(?:White|#FFFFFFFF|#FFFFFF)\"";
        var filled = new[] { "AccentPrimary", "SystemSuccess" };
        var offences = new List<string>();

        foreach (var brush in filled)
        {
            var resource = $"\"{{(?:Dynamic|Static)Resource {brush}(?:Brush)?}}\"";

            // The inline form: one element carrying both attributes.
            foreach (var element in axaml.Split('<'))
            {
                if (Regex.IsMatch(element, $"(?<![\\w.])(?:Foreground|Fill)={white}")
                    && Regex.IsMatch(element, $"Background={resource}"))
                {
                    offences.Add($"{label}: inline on {brush}");
                }
            }

            // The style form: two setters in one block, which the split above cannot see together.
            foreach (Match block in Regex.Matches(axaml, "<Style\\b.*?</Style>", RegexOptions.Singleline))
            {
                if (Regex.IsMatch(block.Value, $"Property=\"(?<![\\w.])(?:Foreground|Fill)\" Value={white}")
                    && Regex.IsMatch(block.Value, $"Property=\"Background\" Value={resource}"))
                {
                    var selector = Regex.Match(block.Value, "Selector=\"([^\"]+)\"");
                    offences.Add($"{label}: style {(selector.Success ? selector.Groups[1].Value : "?")} on {brush}");
                }
            }
        }

        return offences;
    }

    [Fact]
    public void NoViewPutsAWhiteLITERALOnAFilledSurface()
    {
        // THE GUARD THAT WOULD HAVE CAUGHT THIS, and its absence is why the first sweep looked
        // complete. The theme tests above read Themes/ only, so they proved every theme OFFERS a
        // readable foreground while seventeen SITES went on ignoring it - ten inline accent, two
        // inline success, five style blocks - the fix and its guard
        // were measuring different things.
        var viewsDirectory = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Views");
        Assert.True(Directory.Exists(viewsDirectory), $"Views moved: {viewsDirectory}");

        var files = Directory.EnumerateFiles(viewsDirectory, "*.axaml", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var offenders = files
            .SelectMany(file => ScanForWhiteOnFilled(File.ReadAllText(file), Path.GetFileName(file)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A white literal sits on a filled surface, unreadable in at least one theme: "
                + string.Join(", ", offenders)
                + ". Use AccentForegroundBrush or SuccessForegroundBrush, which are per-theme.");
    }

    [Fact]
    public void TheViewScanFindsBOTHSpellings_AndLeavesErrorRedAlone()
    {
        // Feeds the REAL function the shapes it hunts. The style case is the one that matters: the
        // scan reported zero against seven live style-driven offenders before this existed.
        Assert.Single(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource AccentPrimaryBrush}\" Foreground=\"White\"/>", "inline"));

        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.x\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource AccentPrimaryBrush}\"/>"
            + "<Setter Property=\"Foreground\" Value=\"White\"/></Style>", "style"));

        // A glyph, where Foreground does not reach and Fill does.
        Assert.Single(ScanForWhiteOnFilled(
            "<Style Selector=\"Button.x\">"
            + "<Setter Property=\"Background\" Value=\"{DynamicResource SystemSuccessBrush}\"/>"
            + "<Setter Property=\"Fill\" Value=\"White\"/></Style>", "glyph"));

        // The Color-key spelling, which this repo actually uses.
        Assert.Single(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource AccentPrimary}\" Foreground=\"White\"/>", "colorkey"));

        // And the deliberate exclusion really is excluded.
        Assert.Empty(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource SystemErrorBrush}\" Foreground=\"White\"/>", "red"));
        Assert.Empty(ScanForWhiteOnFilled(
            "<Button Background=\"{DynamicResource SystemError}\" Foreground=\"White\"/>", "redcolor"));
    }

    [Fact]
    public void PlainWhiteWouldFailOnEVERYThemesSuccessFill()
    {
        // The control for the success token, and a stronger claim than the accent one: white fails on
        // green in all FOUR themes, not three. Without this the comment's numbers are asserted
        // nowhere and could drift from the themes they describe.
        var failures = Themes.Count(theme =>
            Contrast(Extract(File.ReadAllText(ThemePath(theme)), "<Color x:Key=\"SystemSuccess\">"), "#FFFFFFFF") < 4.5);

        Assert.Equal(Themes.Length, failures);
    }
}
