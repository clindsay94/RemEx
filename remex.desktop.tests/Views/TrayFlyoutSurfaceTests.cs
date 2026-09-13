using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards the Material surface migration of <c>TrayFlyoutWindow.axaml</c> (RemEx-tzl85): the type
/// scale, the presence <c>material:Badged</c> and the <c>MaterialIcon</c> close button. Source scan,
/// not a rendering test — there is no headless Avalonia harness here (see
/// <see cref="TypographyVocabularyTests"/>).
/// </summary>
public class TrayFlyoutSurfaceTests
{
    private static readonly string ViewPath = Path.Combine(RepoRoot(), "remex.desktop", "Views", "TrayFlyoutWindow.axaml");

    [Fact]
    public void NoInlineFontSizeSurvives()
    {
        var text = File.ReadAllText(ViewPath);
        Regex.Matches(text, @"FontSize=""").Should().BeEmpty(
            "every TextBlock in TrayFlyoutWindow.axaml should be on a type-scale Theme, not an inline size");
    }

    [Fact]
    public void EveryTextBlockCarriesATypeScaleTheme()
    {
        var text = File.ReadAllText(ViewPath);
        var textBlocks = Regex.Matches(text, @"<TextBlock(?:\.[A-Za-z]+)?\b[^>]*>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(m.Value, @"\s+", " "));

        foreach (var element in textBlocks)
        {
            // TextBlock.Text property-element syntax (e.g. <TextBlock.Text>) carries no attributes
            // of its own and is not the element the Theme lives on - skip it.
            if (element.StartsWith("<TextBlock.")) continue;

            element.Should().MatchRegex(@"Theme=""\{StaticResource \w+TextBlock\}""",
                $"every TextBlock should carry a vocabulary Theme, but found: {element}");
        }
    }

    [Fact]
    public void PresenceIsAMaterialBadgedBoundToPhonePresence()
    {
        var text = File.ReadAllText(ViewPath);

        text.Should().NotContain("<Ellipse", "the status-dot Ellipse should be replaced by material:Badged");

        var badged = Regex.Match(text, @"<material:Badged\b[^>]*>", RegexOptions.Singleline);
        badged.Success.Should().BeTrue("TrayFlyoutWindow.axaml should carry a material:Badged presence indicator");

        var normalized = Regex.Replace(badged.Value, @"\s+", " ");
        normalized.Should().Contain("Classes=\"presence\"");
        normalized.Should().MatchRegex(@"Classes\.connected=""\{Binding Presence\.IsPhoneAttached\}""");
    }

    [Fact]
    public void CloseButtonUsesAMaterialIconNotAGlyph()
    {
        var text = File.ReadAllText(ViewPath);

        text.Should().NotContain("&#x2715;", "the literal close glyph should be replaced by a MaterialIcon");
        text.Should().NotContain("✕", "no raw close-glyph character should survive either");

        var closeButton = Regex.Match(text,
            @"<Button Grid\.Column=""4""[^>]*Click=""OnCloseFlyout""[^>]*>.*?</Button>",
            RegexOptions.Singleline);
        closeButton.Success.Should().BeTrue("the close button should still exist");
        closeButton.Value.Should().Contain("Kind=\"Close\"");
    }

    [Fact]
    public void NoMaterialIconCarriesAFillAttribute()
    {
        var text = File.ReadAllText(ViewPath);
        Regex.Matches(text, @"<mi:MaterialIcon\b[^>]*>", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Should().OnlyContain(icon => !icon.Contains("Fill="),
                "MaterialIcon does not honour Fill - it does nothing and is a leftover from Path icons");
    }

    [Fact]
    public void FlyoutSensorsAreCanvasCardsInAScrollingWrapPanel()
    {
        var text = File.ReadAllText(ViewPath);

        text.Should().Contain("ctrl:SensorCardContent",
            "the flyout should render pinned sensors with the shared card control, not its own markup");
        text.Should().Contain("<WrapPanel",
            "cards should lay out in a wrapping grid, not a horizontal strip");

        // Flyout D2 .1 (RemEx-4kv0g.18.5): the cards row binds FlyoutSensors, not
        // HomeViewModel.PinnedSensors directly, so a hidden-sensor setting can exclude a pin without
        // touching Home's own collection.
        text.Should().Contain("ItemsSource=\"{Binding FlyoutSensors}\"",
            "the cards ItemsControl must bind the flyout's own filtered collection");
        text.Should().Contain("IsVisible=\"{Binding !!FlyoutSensors.Count}\"",
            "the cards row's collapse-when-empty check must follow the same filtered collection");

        var scrollViewer = Regex.Match(text, @"<ScrollViewer\b[^>]*>", RegexOptions.Singleline);
        scrollViewer.Success.Should().BeTrue("the cards should sit inside a ScrollViewer");
        var normalizedScrollViewer = Regex.Replace(scrollViewer.Value, @"\s+", " ");
        normalizedScrollViewer.Should().Contain("x:Name=\"CardsScrollViewer\"",
            "ApplyMode (fix round 2) needs a named element to relax MaxHeight to PositiveInfinity while pinned");
        normalizedScrollViewer.Should().Contain("HorizontalScrollBarVisibility=\"Disabled\"");
        normalizedScrollViewer.Should().MatchRegex(
            @"MaxHeight=""\{x:Static svc:TrayFlyoutGeometry\.CardsMaxHeight\}""",
            "this is only the TRANSIENT starting value; ApplyMode overrides it at runtime per mode");

        var card = Regex.Match(text, @"<Border Classes=""flyout-card""[^>]*>", RegexOptions.Singleline);
        card.Success.Should().BeTrue("the card host should be a Border.flyout-card");
        var normalizedCard = Regex.Replace(card.Value, @"\s+", " ");
        normalizedCard.Should().Contain("Width=\"200\"");
        normalizedCard.Should().Contain("Height=\"150\"");
        normalizedCard.Should().Contain("Classes.family-primary=\"{Binding IsPrimaryFamily}\"");
        normalizedCard.Should().Contain("Classes.family-secondary=\"{Binding IsSecondaryFamily}\"");
        normalizedCard.Should().Contain("Classes.family-tertiary=\"{Binding IsTertiaryFamily}\"");
        normalizedCard.Should().Contain("Classes.family-neutral=\"{Binding IsNeutralFamily}\"");
        normalizedCard.Should().Contain("Classes.family-custom=\"{Binding IsCustomTheme}\"");

        text.Should().NotContain("Classes=\"tray-sensor\"", "the old strip's host class should be gone");
        text.Should().NotContain("Width=\"44\" Height=\"16\"", "the old strip's 44x16 sparkline should be gone");

        // Structural nesting (fix round 3, whole-branch review): a flat regex scan cannot tell
        // apart "these elements all exist somewhere in the file" from "they nest the way the spec
        // describes" — ScrollViewer > ItemsControl > ItemsPanel WrapPanel, ItemTemplate >
        // Border.flyout-card > ctrl:SensorCardContent. Parsed once so dropping or misplacing the
        // Sensor binding (the one thing that actually puts a sensor's data on the card) fails this
        // test instead of surviving a substring check.
        var doc = XDocument.Parse(text);
        XNamespace av = "https://github.com/avaloniaui";
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace ctrlNs = "using:Remex.Desktop.Controls";

        var namedScrollViewer = doc.Descendants(av + "ScrollViewer")
            .SingleOrDefault(sv => (string?)sv.Attribute(xNs + "Name") == "CardsScrollViewer");
        namedScrollViewer.Should().NotBeNull("the cards ScrollViewer must carry x:Name=\"CardsScrollViewer\"");

        var scrollViewerItemsControl = namedScrollViewer!.Elements(av + "ItemsControl").SingleOrDefault();
        scrollViewerItemsControl.Should().NotBeNull(
            "CardsScrollViewer's content must be exactly one ItemsControl, not some other wrapper");

        var itemsPanelProperty = scrollViewerItemsControl!.Element(av + "ItemsControl.ItemsPanel");
        itemsPanelProperty.Should().NotBeNull();
        itemsPanelProperty!.Descendants(av + "WrapPanel").Should().ContainSingle(
            "the ItemsControl's ItemsPanel must be a WrapPanel, not the old horizontal StackPanel");

        var itemTemplateProperty = scrollViewerItemsControl.Element(av + "ItemsControl.ItemTemplate");
        itemTemplateProperty.Should().NotBeNull();
        var dataTemplate = itemTemplateProperty!.Element(av + "DataTemplate");
        dataTemplate.Should().NotBeNull("the item template must be a DataTemplate");

        var itemBorder = dataTemplate!.Element(av + "Border");
        itemBorder.Should().NotBeNull("the item template's root must be the flyout-card Border directly");
        ((string?)itemBorder!.Attribute("Classes")).Should().Be("flyout-card");

        var cardContentElement = itemBorder.Element(ctrlNs + "SensorCardContent");
        cardContentElement.Should().NotBeNull(
            "the Border's content must be exactly one ctrl:SensorCardContent - the shared card control");
        ((string?)cardContentElement!.Attribute("Sensor")).Should().Be("{Binding}",
            "every flyout card must bind Sensor to the item - drop this and every card renders empty");

        // Padding (fix round 3): reproduces DraggableCard's own content inset
        // (Themes/Shared/DraggableCard.axaml:57) so the card's title/sparkline/plates sit inset
        // from the rounded corners instead of edge-to-edge.
        ((string?)itemBorder.Attribute("Padding")).Should().Be(
            "{Binding $self.CornerRadius, Converter={x:Static conv:CornerRadiusToMarginConverter.Instance}}",
            "the flyout card needs the same corner-radius content inset DraggableCard gives the canvas card");
    }

    /// <summary>
    /// Flyout D2 .1 (RemEx-4kv0g.18.5): the six-tile grid became a wrapping, icon-only toolbar row
    /// with app shortcuts after a divider. Parsed structurally, like
    /// <see cref="FlyoutSensorsAreCanvasCardsInAScrollingWrapPanel"/>'s card nesting checks, so a
    /// template that exists somewhere in the file but is not actually wired to the right item type
    /// fails here instead of passing a flat substring scan.
    /// </summary>
    [Fact]
    public void ActionsAreAnIconOnlyToolbarRow()
    {
        var text = File.ReadAllText(ViewPath);

        text.Should().NotContain("x:Key=\"TrayTileFace\"",
            "the shared tile-face DataTemplate is gone - each toolbar item template draws its own content");
        text.Should().NotContain("<UniformGrid",
            "the fixed-column tile grid is gone - the toolbar wraps instead of committing to a column count");

        var doc = XDocument.Parse(text);
        XNamespace av = "https://github.com/avaloniaui";
        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace miNs = "using:Material.Icons.Avalonia";

        var contentGrid = doc.Descendants(av + "Grid")
            .Single(g => (string?)g.Attribute(xNs + "Name") == "ContentGrid");

        var toolbar = contentGrid.Elements(av + "ItemsControl")
            .SingleOrDefault(ic => (string?)ic.Attribute("ItemsSource") == "{Binding ToolbarItems}");
        toolbar.Should().NotBeNull("the toolbar row must be an ItemsControl bound to ToolbarItems");

        var toolbarPanelProperty = toolbar!.Element(av + "ItemsControl.ItemsPanel");
        toolbarPanelProperty.Should().NotBeNull();
        toolbarPanelProperty!.Descendants(av + "WrapPanel").Should().ContainSingle(
            "the toolbar's ItemsPanel must be a WrapPanel, not the old UniformGrid");

        var dataTemplatesProperty = toolbar.Element(av + "ItemsControl.DataTemplates");
        dataTemplatesProperty.Should().NotBeNull(
            "the toolbar must pick a template per item type via DataTemplates, not one static ItemTemplate");

        var templates = dataTemplatesProperty!.Elements(av + "DataTemplate").ToList();
        templates.Should().HaveCount(3, "one DataTemplate each for TrayTile, TrayToolbarDivider and TrayShortcut");

        // TrayTile: icon-only, tooltip + accessible name on Label, the Power submenu intact.
        var tileTemplate = templates.Single(t => (string?)t.Attribute(xNs + "DataType") == "vm:TrayTile");
        tileTemplate.Descendants(av + "TextBlock").Should().BeEmpty(
            "the toolbar tile is icon-only - no label TextBlock should survive from the old tile face");
        tileTemplate.Descendants(miNs + "MaterialIcon").Should().Contain(
            icon => (string?)icon.Attribute("Kind") == "{Binding Icon}",
            "the tile must still render its glyph from TrayTile.Icon");

        var plainTileButton = tileTemplate.Descendants(av + "Button")
            .SingleOrDefault(b => (string?)b.Attribute("IsVisible") == "{Binding !HasSubmenu}");
        plainTileButton.Should().NotBeNull("the plain (non-submenu) tile button must still exist");
        ((string?)plainTileButton!.Attribute("ToolTip.Tip")).Should().Be("{Binding Label}",
            "the enabled case's tooltip is the Button's own Label, per the Panel/Button precedence");
        ((string?)plainTileButton.Attribute("AutomationProperties.Name")).Should().Be("{Binding Label}");
        ((string?)plainTileButton.Attribute("Command")).Should().Be("{Binding Command}");
        ((string?)plainTileButton.Attribute("Click")).Should().Be("OnTileClicked");

        var submenuButton = tileTemplate.Descendants(av + "Button")
            .SingleOrDefault(b => (string?)b.Attribute("IsVisible") == "{Binding HasSubmenu}");
        submenuButton.Should().NotBeNull("the Power submenu button must still exist");
        var menuFlyout = submenuButton!.Descendants(av + "MenuFlyout").SingleOrDefault();
        menuFlyout.Should().NotBeNull("the submenu tile must still carry its MenuFlyout");
        menuFlyout!.Elements(av + "MenuItem").Should().HaveCount(4,
            "Restart/Shutdown/SignOut/Hibernate must all still be reachable from the glyph button");

        // TrayToolbarDivider: a thin vertical rule, not a tile.
        var dividerTemplate = templates.Single(t => (string?)t.Attribute(xNs + "DataType") == "vm:TrayToolbarDivider");
        dividerTemplate.Descendants(av + "Border").Should().ContainSingle(
            "the divider must render as a single Border rule");

        // TrayShortcut: an Image through the base64 converter, tooltip/name on DisplayName.
        var shortcutTemplate = templates.Single(t => (string?)t.Attribute(xNs + "DataType") == "vm:TrayShortcut");
        var shortcutButton = shortcutTemplate.Element(av + "Button");
        shortcutButton.Should().NotBeNull("the shortcut must render as a single Button, matching the tile shape");
        ((string?)shortcutButton!.Attribute("ToolTip.Tip")).Should().Be("{Binding DisplayName}");
        ((string?)shortcutButton.Attribute("AutomationProperties.Name")).Should().Be("{Binding DisplayName}");
        ((string?)shortcutButton.Attribute("Command")).Should().Be("{Binding LaunchCommand}");

        var shortcutImage = shortcutButton.Descendants(av + "Image").SingleOrDefault();
        shortcutImage.Should().NotBeNull("the shortcut's icon must be an Image, not a MaterialIcon glyph");
        ((string?)shortcutImage!.Attribute("Source")).Should().Contain("Base64ToImageConverter",
            "the shortcut icon must decode through the same converter the App Launcher page uses");
    }

    /// <summary>
    /// Fix round 3 (whole-branch review): <see cref="TrayFlyoutGeometry.ChromeSideInset"/> and
    /// <see cref="TrayFlyoutGeometry.CardsPanelInset"/> are hand-derived from four XAML margins.
    /// This reads those four literals back out of the file and checks the arithmetic against the
    /// constants, so a margin changed in the XAML without updating the constant (or vice versa)
    /// fails here instead of silently making the column-fit promise (DefaultWidth/MaxWidth) wrong.
    /// </summary>
    [Fact]
    public void ChromeInsetConstantsMatchTheXamlMarginsTheyMirror()
    {
        var text = File.ReadAllText(ViewPath);

        // Outer Border (the transparent-window shadow-and-rounding frame around the card). The
        // Background key changed to FlyoutGlassBrush in RemEx-4kv0g.18.5 (Flyout D2 .1) - same
        // Border, only the brush key moved.
        var outerBorderMargin = double.Parse(Regex.Match(text,
                @"<Border Background=""\{DynamicResource FlyoutGlassBrush\}""[^>]*Margin=""(?<v>[\d.]+)""")
            .Groups["v"].Value);

        // ContentGrid (the named Grid the header/cards/toolbar rows live in).
        var contentGridMargin = double.Parse(Regex.Match(text,
                @"<Grid x:Name=""ContentGrid""[^>]*Margin=""(?<v>[\d.]+)""")
            .Groups["v"].Value);

        // The cards ItemsControl's own Margin="H,V" - only the horizontal half feeds CardsPanelInset.
        var itemsControlHorizontalMargin = double.Parse(Regex.Match(text,
                @"<ItemsControl ItemsSource=""\{Binding FlyoutSensors\}"" Margin=""(?<h>[\d.]+),[\d.]+""")
            .Groups["h"].Value);

        // The flyout-card Border's own Margin (all four sides equal - single-value shorthand).
        var cardMargin = double.Parse(Regex.Match(text,
                @"<Border Classes=""flyout-card""[^>]*Margin=""(?<v>[\d.]+)""")
            .Groups["v"].Value);

        TrayFlyoutGeometry.ChromeSideInset.Should().Be(2 * outerBorderMargin + 2 * contentGridMargin,
            "ChromeSideInset is the outer Border's Margin plus the content Grid's Margin, each side");
        TrayFlyoutGeometry.CardsPanelInset.Should().Be(2 * itemsControlHorizontalMargin,
            "CardsPanelInset is the cards ItemsControl's own horizontal Margin, both sides");
        TrayFlyoutGeometry.CardPitch.Should().Be(200 + 2 * cardMargin,
            "CardPitch is the flyout-card's own Width (200, asserted elsewhere) plus its Margin, both sides");
    }

    [Fact]
    public void BehaviouralAnchorsAreUntouched()
    {
        var text = File.ReadAllText(ViewPath);

        // OnDeactivated is wired in the code-behind constructor (Deactivated += OnDeactivated), not
        // in markup - it has no Click handler here, so it is not asserted against the .axaml text.
        text.Should().Contain("OnHeaderPressed");
        text.Should().Contain("OnResizePressed");
        text.Should().Contain("OnTogglePin");
        text.Should().Contain("IsChecked=\"{Binding IsPinned, Mode=OneWay}\"");
    }

    /// <summary>
    /// Repo root via <c>[CallerFilePath]</c> - matches the pattern used by
    /// <see cref="TypographyVocabularyTests"/> so this file survives being moved or run from a
    /// different working directory.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
