using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Source-scrape regression guards for RemEx-alwfa.1's seed-driven splash: the ordering and wiring
/// these assertions pin cannot be observed any other way without a live Skia render (no headless
/// render exists for this control — see <c>ui-verify</c>).
/// </summary>
public sealed class SplashSourceScrapeTests
{
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void SkiaSplashControl_ReadsTheSidecarBeforeTheTimerStarts()
    {
        var source = Read("remex.desktop/Controls/Splash/SkiaSplashControl.cs");

        var applyIndex = source.IndexOf("SplashBrand.ApplyPalette(SplashPaletteResolver.ResolveFromSidecar())", StringComparison.Ordinal);
        var timerStartIndex = source.IndexOf("_timer.Start();", StringComparison.Ordinal);

        applyIndex.Should().BeGreaterThan(-1, "the palette must be applied somewhere in this file");
        timerStartIndex.Should().BeGreaterThan(-1, "the timer must still start somewhere in this file");
        applyIndex.Should().BeLessThan(timerStartIndex,
            "the sidecar-derived palette must be applied before the first frame is timed, not after");
    }

    [Fact]
    public void SkiaSplashControl_FinishesOnTheVariantsOwnCompletion()
    {
        // Live Handshake ends when the app is ready, never on a fixed timer (RemEx-8g6n0.2). The host
        // must ask the variant, and the old Duration comparison must be gone - it would hold every
        // live splash to its CAP + EXIT upper bound.
        var source = Read("remex.desktop/Controls/Splash/SkiaSplashControl.cs");

        source.Should().Contain("_variant.IsComplete((float)elapsed)");
        source.Should().NotContain("_elapsed >= _variant.Duration");
    }

    [Fact]
    public void SkiaSplashControl_StartsTheLiveClockFromTheFirstRenderedFrame()
    {
        // SplashClockTests pins the rule; this pins that the host actually feeds it: the draw op marks
        // the first composed frame, and the tick advances the clock rather than a raw accumulator.
        var source = Read("remex.desktop/Controls/Splash/SkiaSplashControl.cs");

        source.Should().Contain("clock.MarkFirstFrame();");
        source.Should().Contain("_clock.Advance(dt);");
        source.Should().Contain("_clock.Reset(live: variant is ILiveSplashVariant);");
        source.Should().Contain("LiveHandshakeField.WarmUp();", "the SkSL compile is warmed off the render path");
    }

    [Fact]
    public void SkiaSplashControl_RunsTheFieldShaderOnlyOnAGpuLease()
    {
        // On a raster lease the field costs ~200 ms a frame; the variant must be told to fall back.
        var source = Read("remex.desktop/Controls/Splash/SkiaSplashControl.cs");

        source.Should().Contain("live.FieldEnabled = lease.GrContext is not null;");
        var fieldIndex = source.IndexOf("live.FieldEnabled = lease.GrContext is not null;", StringComparison.Ordinal);
        var renderIndex = source.IndexOf("variant.Render(canvas, w, h, t, dt);", StringComparison.Ordinal);
        fieldIndex.Should().BeLessThan(renderIndex, "the GPU check has to land before the frame it governs");
    }

    [Fact]
    public void SkiaSplashControl_LiveSkipHandsOffThroughTheVariantAndKeepsTheOldFadeForTheFilms()
    {
        var source = Read("remex.desktop/Controls/Splash/SkiaSplashControl.cs");

        source.Should().Contain("live.RequestSkip();", "a click on Live Handshake opens the portal at once");
        source.Should().Contain("_skipping = true;", "the fixed-length films keep their short skip fade");
        source.Should().Contain("\"LiveHandshake\" => new LiveHandshakeVariant(new LocalizedHandshakeText()),");
    }

    [Fact]
    public void ApplyAndSave_WritesTheLastSeedSidecar()
    {
        var source = Read("remex.desktop/ViewModels/CustomizationViewModel.cs");

        var applyAndSaveIndex = source.IndexOf("private void ApplyAndSave()", StringComparison.Ordinal);
        applyAndSaveIndex.Should().BeGreaterThan(-1);

        // ApplyAndSave is the last method-looking construct scraped here is not attempted; instead
        // just require the write call to exist somewhere after the method starts, which is
        // sufficient given there is exactly one ApplyAndSave in this file. Matches the call with its
        // logger argument (RemEx-alwfa.1, Opus review MEDIUM: a failed sidecar write must actually
        // log in Release, which needs the view model's logger passed through).
        var sidecarWriteIndex = source.IndexOf("LastSeedSidecar.WriteAsync(settings, _logger)", applyAndSaveIndex, StringComparison.Ordinal);
        sidecarWriteIndex.Should().BeGreaterThan(-1, "ApplyAndSave must refresh the splash's last-seed sidecar on every live change");
    }

    [Fact]
    public void DashboardLayoutService_WritesTheSidecarAfterLoadApplies_Customization()
    {
        var source = Read("remex.desktop/Services/DashboardLayoutService.cs");

        var applyIndex = source.IndexOf("_themeService.ApplyCustomization(profile.Customization);", StringComparison.Ordinal);
        applyIndex.Should().BeGreaterThan(-1);

        var sidecarWriteIndex = source.IndexOf("LastSeedSidecar.WriteAsync(profile.Customization", applyIndex, StringComparison.Ordinal);
        sidecarWriteIndex.Should().BeGreaterThan(-1,
            "an existing install must get a last-seed sidecar the first time it loads a profile under this bead");
    }

    [Fact]
    public void BrandMark_NoLongerUsesTheFixedWindowFillForItsMarkGradient()
    {
        var source = Read("remex.desktop/Controls/BrandMark.cs");

        source.Should().NotContain("private static readonly IBrush WindowFill",
            "the mark's own fill must come from the live theme, not the fixed brand constant");
        source.Should().Contain("AccentPrimary", "the mark gradient's start stop must come from the live theme's primary");
        source.Should().Contain("PaletteTertiary", "the mark gradient's end stop must come from the live theme's tertiary");
    }
}
