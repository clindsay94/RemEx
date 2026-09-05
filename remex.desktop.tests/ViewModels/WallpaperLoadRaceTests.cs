using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Guards the wallpaper decode race fix (RemEx-8twk0.5): a generation counter that discards a
/// stale decode instead of letting it win, an in-flight-path guard so a blur-only tick does not
/// launch a duplicate decode, a deferred dispose of the bitmap a frame may still be compositing
/// against, and a try/catch around the post-decode handling so a faulting property setter or
/// notification does not fault the fire-and-forget load task unobserved.
/// </summary>
/// <remarks>
/// A SOURCE SCAN, deliberately, matching <see cref="ShellPresencePulseTests"/>'s treatment of
/// <c>ShellViewModel</c>. Its constructor takes a full DI graph (<c>DashboardLayoutService</c>,
/// <c>ThemeService</c>, <c>HardwareThemeService</c>, <c>ConnectionViewModel</c>,
/// <c>IServiceProvider</c>) and, transitively through <c>CanvasDashboardViewModel.InitializeAsync</c>,
/// an <c>await Dispatcher.UIThread.InvokeAsync(...)</c> — this test assembly has no
/// Avalonia.Headless reference, so nothing ever pumps that dispatcher and the await never
/// completes. A behavioral test built around <c>TaskCompletionSource</c> fakes for the decode
/// (as the handoff for this fix asked for) would need to actually construct a <c>ShellViewModel</c>
/// to invoke <c>RefreshWallpaperBackdrop</c>/<c>LoadWallpaperAsync</c> on, which hangs before any
/// assertion runs. Reading the wiring is what every other test touching this class already does
/// instead.
/// </remarks>
public class WallpaperLoadRaceTests
{
    [Fact]
    public void ADuplicateRefreshForThePathAlreadyLoadingDoesNotLaunchASecondDecode()
    {
        var body = ExtractMethod(ShellViewModelSource(), "RefreshWallpaperBackdrop");

        body.Should().MatchRegex(
            @"string\.Equals\(path,\s*_wallpaperPathLoading,\s*StringComparison\.OrdinalIgnoreCase\)\)\s*\{\s*[^}]*return;\s*\}",
            "a refresh for the path already in flight (e.g. a blur-only tick) must return before " +
            "launching a second decode of it");
    }

    [Fact]
    public void LaunchingANewDecodeBumpsTheGenerationAndPassesItToTheLoad()
    {
        var body = ExtractMethod(ShellViewModelSource(), "RefreshWallpaperBackdrop");

        // The in-flight guard above has to run BEFORE this, or every blur tick would still bump
        // the generation and discard its own in-flight decode.
        var loadingIndex = body.IndexOf("_wallpaperPathLoading = path;", System.StringComparison.Ordinal);
        var bumpIndex = body.IndexOf("var generation = ++_wallpaperLoadGeneration;", System.StringComparison.Ordinal);
        loadingIndex.Should().BeGreaterThan(-1, "a new decode has to record the path it is loading");
        bumpIndex.Should().BeGreaterThan(loadingIndex, "the in-flight path must be recorded before the generation is bumped");

        body.Should().Contain("LoadWallpaperAsync(settings, path, generation)",
            "the captured generation has to reach the load so it can tell a stale completion from the winning one");
    }

    [Fact]
    public void ACompletingLoadDiscardsItsResultWhenAnewerRequestSupersededIt()
    {
        var body = ExtractMethod(ShellViewModelSource(), "LoadWallpaperAsync");

        body.Should().MatchRegex(
            @"if\s*\(generation\s*!=\s*_wallpaperLoadGeneration\)\s*\{\s*[^}]*bitmap\?\.Dispose\(\);\s*[^}]*return;",
            "a decode whose generation no longer matches the live one has to dispose its bitmap " +
            "and return without touching WallpaperBitmap, _wallpaperPathLoaded, or _wallpaperPathFailed");

        var staleCheckIndex = body.IndexOf("generation != _wallpaperLoadGeneration", System.StringComparison.Ordinal);
        var swapIndex = body.IndexOf("WallpaperBitmap = bitmap;", System.StringComparison.Ordinal);
        staleCheckIndex.Should().BeGreaterThan(-1);
        swapIndex.Should().BeGreaterThan(staleCheckIndex,
            "the staleness check has to run before the live bitmap is ever overwritten, or a slow " +
            "superseded decode can still win the swap");
    }

    [Fact]
    public void TheSupersededPreviousBitmapIsDisposedOnlyAfterTheNextUiFrame()
    {
        var body = ExtractMethod(ShellViewModelSource(), "LoadWallpaperAsync");

        body.Should().Contain("Dispatcher.UIThread.Post(() => previous.Dispose(), DispatcherPriority.Background);",
            "disposing the previous bitmap synchronously in the swap can hand the Image control a " +
            "disposed bitmap it is still compositing this frame — the dispose has to be deferred");
    }

    [Fact]
    public void PostDecodeHandlingIsGuardedSoAFaultingSetterOrNotificationCannotFaultTheLoadTaskUnobserved()
    {
        var body = ExtractMethod(ShellViewModelSource(), "LoadWallpaperAsync");

        Regex.Matches(body, @"catch\s*\(Exception ex\)").Count.Should().BeGreaterThanOrEqualTo(2,
            "the decode itself already has its own catch inside Task.Run — everything after the " +
            "await (property setters, localization, the FailWallpaper notification) needs a second " +
            "one wrapping it, or an exception there faults this fire-and-forget task unobserved");

        // The decode-failure branch must still be reachable inside that guard, not swallowed by it.
        body.Should().Contain("FailWallpaper(settings, path);",
            "a failed decode still has to reach FailWallpaper — the new try/catch guards what " +
            "happens after, not the decode failure path itself");
    }

    [Fact]
    public void DisposeReleasesTheWallpaperBitmap()
    {
        ExtractMethod(ShellViewModelSource(), "Dispose").Should()
            .MatchRegex(@"WallpaperBitmap\?\.Dispose\(\);\s*WallpaperBitmap = null;",
                "ShellViewModel.Dispose has to release the decoded wallpaper bitmap like it already " +
                "releases every other resource it owns");
    }

    /// <summary>
    /// Everything from a method's opening brace to the matching close at class indent (four
    /// spaces). Same heuristic <see cref="ShellPresencePulseTests"/>, <c>PaletteTransitionSuppressionTests</c>
    /// and <c>CommandPaletteLightDismissTests</c> use, for the same reason.
    /// </summary>
    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape — update this test");
        return match.Value;
    }

    private static string ShellViewModelSource([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "ViewModels", "ShellViewModel.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
