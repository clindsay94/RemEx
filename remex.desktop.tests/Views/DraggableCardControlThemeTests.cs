using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using FluentAssertions;
using Remex.Desktop.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the DraggableCard chrome move (RemEx-9iz00.3): the chrome and the RemEx-la0rk elevation
/// ramp live in a ControlTheme at <c>Themes/Shared/DraggableCard.axaml</c>, merged from
/// <c>App.axaml</c>, and <c>CanvasView.axaml</c> no longer owns a Template or any PART name for it.
/// </summary>
/// <remarks>
/// No headless render exists for this suite (see <see cref="ElevationStateTests"/>), so these are
/// source-scanning guards.
/// </remarks>
public class DraggableCardControlThemeTests
{
    private const string ThemeAvaresSource = "avares://Remex.Desktop/Themes/Shared/DraggableCard.axaml";

    [Fact]
    public void ThemeFileExistsAndDeclaresTheControlThemeKeyedToDraggableCard()
    {
        var text = ReadThemeFile();
        text.Should().NotBeEmpty("Themes/Shared/DraggableCard.axaml must exist and have content");

        text.Should().Contain(
            "ControlTheme x:Key=\"{x:Type ctrl:DraggableCard}\"",
            "the theme must be keyed so it is picked up implicitly by every DraggableCard");
        text.Should().Contain(
            "TargetType=\"ctrl:DraggableCard\"",
            "the ControlTheme must target DraggableCard");
    }

    [Fact]
    public void ThemeFileCarriesTheSurfaceBorderResizeThumbAndMarginConverter()
    {
        var text = ReadThemeFile();

        text.Should().Contain("Name=\"PART_SurfaceBorder\"",
            "the surface border must keep its name for the elevation selectors and DraggableCard.cs to find");
        text.Should().Contain("Name=\"PART_ResizeThumb\"",
            "the resize thumb must keep its name for DraggableCard.OnApplyTemplate to find");
        // The whole binding, not just the converter name: dropping the TemplatedParent source
        // still compiles and silently zeroes the content inset.
        text.Should().Contain(
            "Margin=\"{Binding CornerRadius, RelativeSource={RelativeSource TemplatedParent}, "
            + "Converter={x:Static conv:CornerRadiusToMarginConverter.Instance}}\"",
            "the ContentPresenter must still inset itself from the card's own corner radius");
    }

    [Fact]
    public void AppAxamlMergesExactlyTheDraggableCardThemeInclude()
    {
        var appAxaml = ReadAppAxaml();

        appAxaml.Should().Contain(
            $"<ResourceInclude Source=\"{ThemeAvaresSource}\"/>",
            "App.axaml must merge the DraggableCard theme by its exact avares:// source");
    }

    [Fact]
    public void CanvasViewNoLongerOwnsATemplateOrEitherPartNameForDraggableCard()
    {
        var text = ReadCanvasView();

        // Scoped to the DraggableCard styles that remain (.selected / .alert-active), so an
        // unrelated Template setter elsewhere in the view cannot trip this guard.
        var draggableCardStyles = Regex.Matches(
                text, @"<Style Selector=""ctrl\|DraggableCard[^""]*"">.*?</Style>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .ToList();
        draggableCardStyles.Should().NotBeEmpty(
            "CanvasView still owns the .selected and .alert-active state styles for DraggableCard");
        draggableCardStyles.Should().OnlyContain(style => !style.Contains("Property=\"Template\""),
            "the DraggableCard template moved to the ControlTheme; no CanvasView style may set one");
        text.Should().NotContain("PART_SurfaceBorder",
            "PART_SurfaceBorder now lives only in the ControlTheme");
        text.Should().NotContain("PART_ResizeThumb",
            "PART_ResizeThumb now lives only in the ControlTheme");
    }

    [Fact]
    public void DraggableCardThemeIncludeSurvivesAThemeSwitchAcrossEveryPreset()
    {
        // Same guard ThemeSwapMergedDictionaryTests applies to Themes/Chrome/WindowChrome.axaml
        // (RemEx-gcqw5): a merged Themes/ include survives SwapBaseTheme only because its source
        // string is not one of the four base theme files, not because of the folder it sits in.
        // Exercised through SwapBaseTheme itself, in the shape App.axaml declares (base theme
        // first, this include after it), so a predicate widened back to a folder prefix fails
        // here rather than only in a name comparison.
        var themeUri = new Uri(ThemeAvaresSource);
        var draggableCardTheme = new ResourceInclude(themeUri) { Source = themeUri };
        var startupUri = ThemeService.BaseThemeUri(AppTheme.Dynamic);
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new ResourceInclude(startupUri) { Source = startupUri });
        resources.MergedDictionaries.Add(draggableCardTheme);

        var presets = Enum.GetValues<AppTheme>();
        presets.Should().NotBeEmpty("AppTheme must have at least one preset to cycle through");

        IResourceProvider? tracked = null;
        foreach (var theme in presets)
        {
            tracked = ThemeService.SwapBaseTheme(
                resources.MergedDictionaries, tracked, ThemeService.BaseThemeUri(theme));

            resources.MergedDictionaries.Should().Contain(draggableCardTheme,
                $"switching to {theme} replaces the base theme and must leave the DraggableCard "
                + "ControlTheme merged, or every dashboard card loses its template on the first switch");
        }
    }

    private static string ReadThemeFile()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Themes", "Shared", "DraggableCard.axaml"));

    private static string ReadAppAxaml()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "App.axaml"));

    private static string ReadCanvasView()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
