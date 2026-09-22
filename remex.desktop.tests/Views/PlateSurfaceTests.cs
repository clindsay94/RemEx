using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Audit RemEx-4kv0g.5.2: <c>GlassBaseDarkBrush</c> is the WINDOW's glass base (tone ≈ 6 Dark /
/// 98 Light). Painted on a plate INSIDE a card it read as a black hole in Dark and a white slab in
/// Light. The in-content plates below must use <c>CardPlateNeutralBrush</c> instead — the
/// container-tier plate the sensor cards already use. Window/dialog/backdrop/drawer/app-bar uses of
/// <c>GlassBaseDarkBrush</c> are explicitly out of scope and must remain untouched. Source-level,
/// matching the other surface tests in this folder: there is no headless render, so a regression
/// here paints wrong silently rather than throwing.
/// </summary>
public class PlateSurfaceTests
{
    [Theory]
    [InlineData("RemoteView.axaml", @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""10"" Padding=""14,12""")]
    public void RemoteViewMacPlateUsesTheContainerTierBrush(string file, string pattern)
        => Markup(file).Should().MatchRegex(pattern, "the MAC-address plate must use the container-tier plate brush");

    [Theory]
    [InlineData(
        @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""12"" Padding=""16"" BorderBrush=""\{DynamicResource CardBorderBrush\}"" BorderThickness=""1"">\s*<StackPanel Spacing=""10"">",
        "the connection status plate")]
    [InlineData(
        @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""12"" Padding=""16"" BorderBrush=""\{DynamicResource CardBorderBrush\}"" BorderThickness=""1"" Margin=""0,0,0,12"">\s*<Grid ColumnDefinitions=""\*,Auto,Auto"">",
        "the shared-root item plate")]
    [InlineData(
        @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""12"" Padding=""16"" BorderBrush=""\{DynamicResource CardBorderBrush\}"" BorderThickness=""1"" Margin=""0,0,0,12"">\s*<Grid ColumnDefinitions=""Auto,\*"">",
        "the paired-device item plate")]
    [InlineData(
        @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""12"" Padding=""16"" BorderBrush=""\{DynamicResource CardBorderBrush\}"" BorderThickness=""1"" Margin=""0,0,0,12"">\s*<StackPanel Spacing=""12"">",
        "the trust-device item plate")]
    public void SettingsViewItemPlatesUseTheContainerTierBrush(string pattern, string because)
        => Markup("SettingsView.axaml").Should().MatchRegex(pattern, because);

    [Fact]
    public void SettingsViewAlertsEmptyStateUsesTheSecondaryTextBrushNotOutline()
    {
        // RemEx-8wpvr.12: TextMutedBrush is the palette's Outline - a border tone, ~3:1 - and the
        // alerts card's empty-state line vanished in it against the wallpaper. As the card's only
        // content it is supporting text, which is OnSurfaceVariant (TextSecondaryBrush).
        Markup("SettingsView.axaml").Should().MatchRegex(
            @"<TextBlock Text=""\{local:Localize Settings_Alerts_Empty\}""[^>]*Foreground=""\{DynamicResource TextSecondaryBrush\}""",
            "the alerts empty-state line must use the supporting-text brush, not the Outline tone");
    }

    [Fact]
    public void SettingsViewQrAndPinBoxesStillUseTheWindowGlassBrush()
    {
        // These two are the window-glass boxes behind a QR code and a PIN — explicitly out of
        // scope (RemEx-4kv0g.5.2 brief). Exactly two survivors, both GlassBaseDarkBrush.
        Count(Markup("SettingsView.axaml"), @"Background=""\{DynamicResource GlassBaseDarkBrush\}""")
            .Should().Be(2, "only the QR and PIN display panels keep the window glass brush");
    }

    [Theory]
    [InlineData(
        @"<Border Grid\.Column=""2"" Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""6"" Padding=""8,3""",
        "the command-palette search plate")]
    [InlineData(
        @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""5"" Padding=""6,2""[^>]*>\s*<TextBlock Text=""Ctrl \+ 1–7""",
        "the first keyboard-hint pill")]
    [InlineData(
        @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""5"" Padding=""6,2""[^>]*>\s*<TextBlock Text=""Ctrl \+ ,""",
        "the second keyboard-hint pill")]
    [InlineData(
        @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""5"" Padding=""6,2""[^>]*>\s*<TextBlock Text=""Esc""",
        "the third keyboard-hint pill")]
    [InlineData(
        @"<Border Grid\.Column=""0"" Width=""34"" Height=""34"" CornerRadius=""10"" Background=""\{DynamicResource CardPlateNeutralBrush\}""",
        "the recent-activity phone card's inner plate")]
    public void HomeViewPlatesUseTheContainerTierBrush(string pattern, string because)
        => Markup("HomeView.axaml").Should().MatchRegex(pattern, because);

    [Fact]
    public void HomeViewHasNoRemainingGlassBaseDarkBrushPlate()
    {
        Count(Markup("HomeView.axaml"), @"\{DynamicResource GlassBaseDarkBrush\}").Should().Be(0,
            "every in-content plate in HomeView was swept — none should still reference the window glass brush");
    }

    [Fact]
    public void RemoteViewHasNoRemainingGlassBaseDarkBrushPlate()
    {
        Count(Markup("RemoteView.axaml"), @"\{DynamicResource GlassBaseDarkBrush\}").Should().Be(0,
            "the MAC-address plate was the only GlassBaseDarkBrush use in RemoteView, and it is swept");
    }

    [Fact]
    public void CanvasViewLatencySparklinePlateUsesTheContainerTierBrush()
    {
        // The small CornerRadius=6 plate under the latency numbers, inside the card - an
        // in-content plate, confirmed distinct from the toolbar strip below.
        Markup("CanvasView.axaml").Should().MatchRegex(
            @"<Border Background=""\{DynamicResource CardPlateNeutralBrush\}"" CornerRadius=""6"" Margin=""0,10,0,0"" Padding=""6"" Height=""60"">",
            "the latency sparkline plate sits inside the card and must use the container-tier brush");
    }

    [Fact]
    public void CanvasViewToolbarStripStillUsesTheWindowGlassBrush()
    {
        // The toolbar strip (Grid.Row="0") is the window/app-bar surface, explicitly out of scope.
        Markup("CanvasView.axaml").Should().MatchRegex(
            @"<Border Grid\.Row=""0"" Background=""\{DynamicResource GlassBaseDarkBrush\}"" Padding=""12 8"">",
            "the toolbar is not an in-content plate and must keep the window glass brush");

        Count(Markup("CanvasView.axaml"), @"\{DynamicResource GlassBaseDarkBrush\}").Should().Be(1,
            "only the toolbar strip should still reference the window glass brush in CanvasView");
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

    private static string Markup(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", fileName));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
