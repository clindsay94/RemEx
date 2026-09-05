using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Mica never rendered on this Avalonia build (RemEx-z94c7: window pixels invariant to wallpaper
/// changes). The spec removes it from the list, the converter and the window plumbing rather than
/// repairing it. Source-text, because the failure it guards is a silent flat surface.
/// </summary>
public class MicaIsGoneTests
{
    [Theory]
    [InlineData("remex.desktop/ViewModels/CustomizationViewModel.cs")]
    [InlineData("remex.desktop/Converters/StringMatchConverter.cs")]
    [InlineData("remex.desktop/Controls/DashboardBackgroundControl.axaml")]
    [InlineData("remex.desktop/MainWindow.axaml.cs")]
    [InlineData("remex.desktop/MainWindow.axaml")]
    public void NoMicaLiteralSurvivesInTheBackgroundModePlumbing(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Regex.IsMatch(text, "\"Mica\"|IsMica|WindowTransparencyLevel\\.Mica|TransparencyLevelHint=\"[^\"]*Mica")
            .Should().BeFalse($"{relativePath} still offers or plumbs Mica, a mode that cannot render (RemEx-z94c7)");
    }

    [Fact]
    public void TheDefaultBackgroundIsAuroraAndMicaIsNotAnOption()
    {
        new Remex.Core.Models.CustomizationSettings().BackgroundMaterial.Should().Be("Aurora");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
