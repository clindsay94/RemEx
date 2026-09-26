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

    /// <summary>
    /// Perf audit P3-68: every apply (every slider settle) used to hand Avalonia a new hint array and,
    /// in Mica, repeat both DWM calls. Each hint assignment is now behind the material-changed check,
    /// and the DWM request behind the light/dark it last landed with.
    /// </summary>
    [Fact]
    public void TheBackdropIsReRequestedOnlyWhenTheEffectiveModeChanged()
    {
        var source = Source();

        var hintAssignments = System.Text.RegularExpressions.Regex.Matches(
            source, @"(?<guard>if \(materialChanged\)\s*)?TransparencyLevelHint = new\[\]");
        hintAssignments.Count.Should().Be(4, "one hint per material branch");
        hintAssignments.Should().OnlyContain(m => m.Groups["guard"].Success,
            "an unguarded hint assignment re-requests the backdrop on every apply");

        source.Should().Contain("MicaBackdrop.Plan(_micaAppliedDark, dark)",
            "the Mica DWM call must only repeat when the light/dark answer changed or it has not landed yet");
    }

    /// <summary>
    /// Live-check B4: a theme-variant change in Mica must clear before re-requesting, the same
    /// AUTO→MAINWINDOW transition Mica→Acrylic→Mica goes through, or Mica does not come back.
    /// </summary>
    [Fact]
    public void AThemeVariantChange_ClearsThenReappliesMica()
    {
        var source = Source();
        var reapply = source.IndexOf("micaRequest == MicaBackdrop.Request.Reapply", StringComparison.Ordinal);
        reapply.Should().BeGreaterThan(-1, "the Mica branch must handle a light/dark flip explicitly");

        var clear = source.IndexOf("MicaBackdrop.Clear(this)", reapply, StringComparison.Ordinal);
        var apply = source.IndexOf("MicaBackdrop.TryApply(this, dark)", reapply, StringComparison.Ordinal);
        clear.Should().BeGreaterThan(reapply, "a flip must clear the DWM backdrop first");
        apply.Should().BeGreaterThan(clear, "and only then request MAINWINDOW again");
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
