using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Guards MainWindow.OnCustomizationApplied's Mica branch (RemEx-rq0xl). Source-level, same
/// constraint as WindowChromeBackdropTests: this suite has no headless Avalonia render, so the
/// window's actual transparency behaviour can only be pinned from source.
/// </summary>
public class MainWindowBackdropTests
{
    [Fact]
    public void TheMicaBranch_RequestsTransparentAndCallsMicaBackdrop_NeverTheMicaHint()
    {
        var source = Source();

        // "settings." prefix, not the bare comparison: "_previousBackgroundMaterial == \"Mica\""
        // (the leaving-Mica tracker, above the if/else chain) ends with the same substring and
        // would otherwise be matched first.
        var micaBranchStart = source.IndexOf("settings.BackgroundMaterial == \"Mica\"", StringComparison.Ordinal);
        micaBranchStart.Should().BeGreaterThan(-1, "OnCustomizationApplied must branch on the Mica material");

        var nextBranch = source.IndexOf("else if", micaBranchStart, StringComparison.Ordinal);
        nextBranch.Should().BeGreaterThan(-1, "the Mica branch must be followed by another branch");
        var micaBranch = source[micaBranchStart..nextBranch];

        micaBranch.Should().Contain("WindowTransparencyLevel.Transparent",
            "Avalonia never asks DWM for the backdrop when given the Mica hint - the window must " +
            "ask for Transparent and let MicaBackdrop make the DWM call itself");
        micaBranch.Should().NotContain("WindowTransparencyLevel.Mica",
            "the Mica hint paints Avalonia's own flat layer instead of a real DWM backdrop " +
            "(docs/REGRESSION-GUARDS.md)");
        micaBranch.Should().Contain("MicaBackdrop.TryApply(",
            "the branch must actually call into MicaBackdrop, not just request Transparent");
    }

    [Fact]
    public void LeavingMica_ClearsTheDwmBackdrop()
    {
        Source().Should().Contain("MicaBackdrop.Clear(this)",
            "leaving Mica for another material must clear the DWM backdrop or it lingers " +
            "underneath Acrylic or a solid fill");
    }

    [Fact]
    public void TheMicaBranch_IsCheckedFirst()
    {
        var source = Source();

        var micaBranchStart = source.IndexOf("settings.BackgroundMaterial == \"Mica\"", StringComparison.Ordinal);
        var acrylicBranchStart = source.IndexOf("settings.BackgroundMaterial == \"Acrylic\"", StringComparison.Ordinal);

        micaBranchStart.Should().BeGreaterThan(-1);
        acrylicBranchStart.Should().BeGreaterThan(-1);
        micaBranchStart.Should().BeLessThan(acrylicBranchStart,
            "Mica must be evaluated before Acrylic so a Mica-capable Windows box does not fall " +
            "through to the Acrylic branch");
    }

    private static string Source() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "MainWindow.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
