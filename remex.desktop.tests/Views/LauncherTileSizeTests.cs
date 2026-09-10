using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The launcher tile size is written down twice, and the two copies must agree (RemEx-u4244).
/// </summary>
/// <remarks>
/// <para>
/// <c>AppLauncherView.axaml</c> sets the WrapPanel's <c>ItemWidth</c>/<c>ItemHeight</c>, which is
/// what actually lays the grid out. <c>AppLauncherView.axaml.cs</c> repeats the same two numbers as
/// constants, which is what <c>LauncherLayoutMath.IndexFromPoint</c> uses to turn a drag pointer
/// position into a destination index. Only a comment held them together.
/// </para>
/// <para>
/// DRIFT HERE IS SILENT. Nothing throws, no binding fails, no log line appears. A dragged tile
/// simply lands in the wrong slot, and because the error is a per-cell offset it compounds down the
/// grid — correct in the first row, one off in the second, several off by the fifth. That reads as
/// "drag-to-rearrange is flaky", which is exactly the kind of report that gets closed as
/// unreproducible.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST because remex.desktop.tests has no headless render and cannot measure a
/// WrapPanel. Reading the number back out of the axaml is the only way to check the value the
/// layout engine will actually see.
/// </para>
/// <para>
/// The tile also has to be big enough for what is drawn inside it, which is why the size moved at
/// all: the icon went from 64px to 80px when the Windows extractor stopped returning 32x32 bitmaps,
/// and the old 160px cell left the launch button 102px of inner height for 104px of content.
/// </para>
/// </remarks>
public class LauncherTileSizeTests
{
    [Fact]
    public void TheCodeBehindTileSizeMatchesTheWrapPanelInTheAxaml()
    {
        var (axamlWidth, axamlHeight) = WrapPanelItemSize();

        axamlWidth.Should().Be(Remex.Desktop.Views.AppLauncherView.ItemWidth,
            "LauncherLayoutMath maps drag positions with the code-behind constant, so a tile that "
            + "lays out at a different width drops every dragged card in the wrong column");

        axamlHeight.Should().Be(Remex.Desktop.Views.AppLauncherView.ItemHeight,
            "same mapping, same failure, one axis over: a mismatched height offsets the drop row");
    }

    [Fact]
    public void TheTileIsTallEnoughForTheIconAndItsLabel()
    {
        var (_, tileHeight) = WrapPanelItemSize();

        // Measured against the template in AppLauncherView.axaml: Card Margin 8 on each side, the
        // action toolbar along the bottom, and the launch Button's own Padding, all of which come
        // off before the icon and its label get any room.
        const double cardMargin = 8 * 2;
        const double actionToolbarHeight = 28; // 28px compact icon-button, Padding 4,0 (RemEx-o2fee)
        const double buttonPadding = 12 * 2;
        const double labelHeight = 16 + 8; // 12pt line box plus its top margin

        var available = tileHeight - cardMargin - actionToolbarHeight - buttonPadding - labelHeight;

        available.Should().BeGreaterThanOrEqualTo(IconEdge(),
            "the icon is given a fixed Width/Height, so a tile too short to hold it does not shrink "
            + "the art — it clips the label underneath it");
    }

    [Fact]
    public void TheIconIsLargeEnoughToJustifyTheHighResolutionExtractor()
    {
        // Pairs with DesktopIconExtractionService: the extractor now produces up to 256px, and
        // AppLauncherViewModel re-extracts anything stored below 64px. An icon drawn at 64 or less
        // would make both of those pointless.
        IconEdge().Should().BeGreaterThan(64,
            "the 32px-only extractor was replaced precisely so the tile could draw larger art");
    }

    private static (double Width, double Height) WrapPanelItemSize()
    {
        var axaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "AppLauncherView.axaml"));

        var match = Regex.Match(axaml, @"<WrapPanel[^>]*ItemWidth=""(?<w>[\d.]+)""[^>]*ItemHeight=""(?<h>[\d.]+)""");
        match.Success.Should().BeTrue(
            "if this stops matching the assertions are vacuous — the WrapPanel or its attribute order moved");

        return (
            double.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture));
    }

    private static double IconEdge()
    {
        var axaml = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "AppLauncherView.axaml"));

        var match = Regex.Match(axaml, @"<Image[^>]*Base64ToImageConverter[^>]*Width=""(?<w>[\d.]+)""");
        match.Success.Should().BeTrue(
            "the launcher tile's Image is identified by its Base64ToImageConverter binding; if that "
            + "moved this guard is measuring nothing");

        return double.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
