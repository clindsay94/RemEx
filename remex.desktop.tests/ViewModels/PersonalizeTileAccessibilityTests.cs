using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>Strategy chips explain themselves on hover, and preset/strategy tiles tell screen readers which one is selected.</summary>
public class PersonalizeTileAccessibilityTests
{
    private static readonly string[] LocaleSuffixes = ["", ".es", ".fr", ".hi", ".id", ".pl", ".pt-BR", ".tr", ".uk"];

    public static IEnumerable<object[]> VariantAndLocale() =>
        from variant in SchemeVariants.All
        from suffix in LocaleSuffixes
        select new object[] { variant, suffix };

    // LocalizationKeyReferenceTests cannot see an interpolated key, so each description is checked by name.
    [Theory]
    [MemberData(nameof(VariantAndLocale))]
    public void EverySchemeVariant_HasADescription_InEveryLanguage(string variant, string suffix)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "Localization", $"Strings{suffix}.resx"));
        var value = doc.Root!.Elements("data")
            .FirstOrDefault(d => (string?)d.Attribute("name") == $"Custom_SchemeDesc_{variant}")?.Element("value")?.Value;

        value.Should().NotBeNullOrWhiteSpace($"Custom_SchemeDesc_{variant} must be translated in Strings{suffix}.resx");
    }

    [Fact]
    public void ASchemeStrip_DescribesItself_AndReportsSelectionOnlyWhileSelected()
    {
        using var strip = new SchemeVariantStripViewModel(SchemeVariants.Vibrant);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)strip).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        strip.Description.Should().NotBeNullOrWhiteSpace();
        strip.SelectionStatus.Should().BeEmpty();

        strip.IsSelected = true;

        strip.SelectionStatus.Should().NotBeNullOrWhiteSpace();
        raised.Should().Contain(nameof(SchemeVariantStripViewModel.SelectionStatus));

        raised.Clear();
        strip.IsSelected = false;

        strip.SelectionStatus.Should().BeEmpty();
        raised.Should().Contain(nameof(SchemeVariantStripViewModel.SelectionStatus));
    }

    [Fact]
    public void APresetTile_ReportsSelectionOnlyWhileSelected()
    {
        using var tile = new SeedPresetTileViewModel(SeedPresetCatalog.Default);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)tile).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tile.SelectionStatus.Should().BeEmpty();

        tile.IsSelected = true;

        tile.SelectionStatus.Should().NotBeNullOrWhiteSpace();
        raised.Should().Contain(nameof(SeedPresetTileViewModel.SelectionStatus));

        raised.Clear();
        tile.IsSelected = false;

        tile.SelectionStatus.Should().BeEmpty();
        raised.Should().Contain(nameof(SeedPresetTileViewModel.SelectionStatus));
    }

    [Fact]
    public void TheTileButtons_BindHelpTextToSelectionStatus_AndTheStripBindsItsTip()
    {
        var views = Path.Combine(RepoRoot(), "remex.desktop", "Views", "Personalize");
        var colour = File.ReadAllText(Path.Combine(views, "PersonalizeColourTab.axaml"));
        var palettes = File.ReadAllText(Path.Combine(views, "PersonalizePalettesTab.axaml"));

        colour.Should().Contain("AutomationProperties.HelpText=\"{Binding SelectionStatus}\"");
        colour.Should().Contain("ToolTip.Tip=\"{Binding Description}\"");
        palettes.Should().Contain("AutomationProperties.HelpText=\"{Binding SelectionStatus}\"");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
