using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the card surface after RemEx-qbzl1 moved it from <c>Border.glass-card</c> onto
/// Material.Styles' <c>Card</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every failure this file guards is SILENT — a wrong-but-valid theme still compiles, still
/// resolves, and still renders something. There is no headless render in this suite, so the only
/// place these invariants can be checked is the source that declares them.
/// </para>
/// <para>
/// Three of them would each turn RemEx's glass shell opaque or flat without a single exception:
/// moving the Card theme out of <c>Application.Resources</c>' own keys, letting ShadowAssist back
/// into the template (it writes BoxShadow as a local value, which outranks every style), or
/// unhooking CornerRadius from <c>CardCornerRadius</c> (the personalization slider's only path).
/// </para>
/// </remarks>
public class CardSurfaceTests
{
    private const string Avalonia = "https://github.com/avaloniaui";
    private const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] ThemePresets =
        { "BaseDarkGlass", "CyberNOC", "Monolith", "SolarFlare" };

    private static readonly string[] ElevationKeys =
        { "Elevation1Shadow", "Elevation2Shadow", "Elevation3Shadow" };

    [Fact]
    public void NoGlassCardClassOrSelectorSurvivesAnywhere()
    {
        // THE SWEEP'S ACCEPTANCE CRITERION, and the reason it needs a test rather than a grep in a
        // commit message: a leftover Classes="glass-card" is not an error. It matches nothing, so
        // the element renders as an unstyled Border and the view still compiles and still opens.
        // That is exactly how HomeView's eleven quick-action buttons sat broken — carrying
        // "glass-card interactive" against a Border-only selector — until this sweep found them.
        var offenders = XamlFiles()
            .Select(file => (file, text: File.ReadAllText(file)))
            .Where(pair => pair.text.Contains("Classes=\"", StringComparison.Ordinal)
                           || pair.text.Contains("Selector=\"", StringComparison.Ordinal))
            .Where(pair => MentionsGlassCardOutsideAComment(pair.text))
            .Select(pair => Path.GetFileName(pair.file))
            .ToList();

        offenders.Should().BeEmpty(
            "glass-card is retired; a leftover class matches no selector and fails by rendering "
            + "an unstyled control rather than by throwing");
    }

    [Fact]
    public void TheCardThemeIsAnOwnKeyOfApplicationResources_NotAMergedDictionary()
    {
        // WHY THIS SHAPE. ThemeService used to clear EVERY merged dictionary except its own
        // override dictionary before inserting the newly selected theme file, so a Card theme
        // reached through a ResourceInclude worked on launch and disappeared the first time anyone
        // switched theme — cards falling back to Material's opaque MaterialCardBackgroundBrush
        // over a translucent mica/acrylic window, erasing the backdrop wherever a card sits, with
        // no exception and no log line. RemEx-gcqw5 fixed the swap itself, and
        // ThemeSwapMergedDictionaryTests guards it, so an own key is no longer the ONLY safe shape
        // — it is still the right one for a single ControlTheme, and moving it would be churn
        // whose failure mode is invisible, so pin it here.
        var resources = AppResources();

        var cardTheme = resources
            .Elements(XName.Get("ControlTheme", Avalonia))
            .SingleOrDefault(theme => theme.Attribute(XName.Get("Key", Xaml))?.Value
                                      == "{x:Type material:Card}");

        cardTheme.Should().NotBeNull(
            "the Card ControlTheme has to stay a direct child of Application.Resources; moving it "
            + "into a merged dictionary is churn whose failure mode is silent");

        cardTheme!.Attribute("TargetType")?.Value.Should().Be("material:Card");
    }

    [Fact]
    public void TheCardTemplateBindsTheElevationRamp_AndKeepsShadowAssistOut()
    {
        // ShadowAssist.ShadowDepth's change handler assigns Border.BoxShadow imperatively, and a
        // local value outranks a style setter in Avalonia. Reintroducing it — the obvious "use the
        // library properly" tidy — would pin every card to Material's fixed black drop shadow and
        // make the App.axaml hover styles below it inert, with the GlowStrength slider quietly
        // driving nothing. The template binds the ramp key directly precisely to avoid that.
        var background = CardTemplateBackgroundBorder();

        background.Attribute("BoxShadow")?.Value.Should().Be("{DynamicResource Elevation1Shadow}",
            "a resting card is elevation level 1, and the shadow has to be set on the template's "
            + "background border because Card has no BoxShadow property of its own");

        CardThemeText().Should().NotContain("ShadowAssist",
            "ShadowAssist writes BoxShadow as a LOCAL value, which would outrank the ramp binding "
            + "here and every hover-level style in App.axaml");
    }

    [Fact]
    public void TheCardKeepsTheUserControllableGeometryAndTheGlassPaint()
    {
        // CustomizationViewModel's corner-radius slider writes CardCornerRadius and animates a
        // "vibrate" preview off it. That only reaches a card while these stay DynamicResource
        // lookups — swapping any of them for a literal is a silent regression in a user-facing
        // control, and Material's own defaults (opaque paper, CornerRadius 4) are what a card
        // falls back to.
        var background = CardTemplateBackgroundBorder();

        foreach (var property in new[] { "Background", "BorderBrush", "BorderThickness", "CornerRadius" })
        {
            background.Attribute(property)?.Value.Should().Be("{TemplateBinding " + property + "}",
                $"{property} has to reach the template from the Card so a call site can still override it");
        }

        var setters = CardThemeSetters();
        setters.Should().Contain(("Background", "{DynamicResource CardBackgroundBrush}"),
            "RemEx cards are translucent glass over the window backdrop, not Material's opaque paper");
        setters.Should().Contain(("CornerRadius", "{DynamicResource CardCornerRadius}"),
            "the personalization corner-radius slider writes CardCornerRadius and has no other path in");
        setters.Should().Contain(("Padding", "0"),
            "every call site sets its own padding; Material's default of 8 would inset all of them");
        setters.Should().Contain(("ClipToBounds", "False"),
            "a clipped Card clips its own shadow — content clipping is InsideClipping's job");
    }

    [Fact]
    public void EveryThemePresetDeclaresTheWholeElevationRamp()
    {
        // A ramp with a hole in it degrades to whatever the fallback resolves, which for a
        // BoxShadows DynamicResource is nothing at all: the card renders flat. Per preset, because
        // the four differ deliberately and a copy-paste that skips one is invisible until someone
        // hovers a card in that one theme.
        foreach (var preset in ThemePresets)
        {
            var text = File.ReadAllText(
                Path.Combine(RepoRoot(), "remex.desktop", "Themes", preset + ".axaml"));

            foreach (var key in ElevationKeys)
            {
                text.Should().Contain($"x:Key=\"{key}\"",
                    $"{preset}.axaml has to declare {key} or cards render flat in that theme");
            }
        }
    }

    [Fact]
    public void ThemeServiceOverridesEveryElevationLevelFromTheSeed()
    {
        // ANTI-DRIFT. The theme files' literals are only what a preset renders with before
        // ApplyCustomization runs; the accent tint and the GlowStrength slider arrive here. A level
        // added to the theme files but not written here would ignore the slider for one card state
        // only — the kind of half-working that reads as "the glow is a bit inconsistent" rather
        // than as a bug.
        var service = File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));

        foreach (var key in ElevationKeys)
        {
            service.Should().Contain($"SetResourceOverrideInternal(\"{key}\"",
                $"{key} is declared in the theme files but never seeded, so it would ignore the "
                + "accent and the GlowStrength slider");
        }
    }

    // ─────────────────────────── plumbing ───────────────────────────

    /// <summary>
    /// True when "glass-card" appears somewhere that is not an XML or C# comment. Prose references
    /// to the retired idiom are legitimate — the point of the sweep is that no LIVE class or
    /// selector still names it.
    /// </summary>
    private static bool MentionsGlassCardOutsideAComment(string text)
        => Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline)
            .Contains("glass-card", StringComparison.Ordinal);

    private static XElement AppResources()
    {
        var app = XDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "remex.desktop", "App.axaml")));

        var resources = app.Root!
            .Element(XName.Get("Application.Resources", Avalonia))!
            .Element(XName.Get("ResourceDictionary", Avalonia));

        resources.Should().NotBeNull("App.axaml declares Application.Resources as a ResourceDictionary");
        return resources!;
    }

    private static XElement CardTheme()
        => AppResources()
            .Elements(XName.Get("ControlTheme", Avalonia))
            .Single(theme => theme.Attribute(XName.Get("Key", Xaml))?.Value == "{x:Type material:Card}");

    private static string CardThemeText() => CardTheme().ToString();

    private static (string Property, string Value)[] CardThemeSetters()
        => CardTheme()
            .Elements(XName.Get("Setter", Avalonia))
            .Select(setter => (setter.Attribute("Property")?.Value ?? string.Empty,
                               setter.Attribute("Value")?.Value ?? string.Empty))
            .ToArray();

    private static XElement CardTemplateBackgroundBorder()
    {
        var border = CardTheme()
            .Descendants(XName.Get("Border", Avalonia))
            .SingleOrDefault(element => element.Attribute("Name")?.Value == "PART_BackgroundBorder");

        border.Should().NotBeNull(
            "the Card template paints and elevates through PART_BackgroundBorder; renaming or "
            + "removing it takes the whole surface with it");
        return border!;
    }

    private static string[] XamlFiles()
        => Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            .ToArray();

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
