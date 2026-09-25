using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// P1-29: a logon-task <c>--minimized</c> start used to build <c>MainWindow</c> (and the whole
/// <c>ShellView</c> tree - theme apply, wallpaper decode) unconditionally, only skipping
/// <c>Show()</c>. Deferred entirely for a minimized start, relying on the SAME lazy-construction
/// helper (<c>BringMainWindowToFront</c>) that <c>docs/REGRESSION-GUARDS.md</c> RG:1032-1049
/// already requires every Desktop-route consumer (including the file-consent dialog) to go through.
/// </summary>
/// <remarks>
/// A SOURCE SCAN, for the same reason the other tests touching <c>App.axaml.cs</c>'s startup path
/// use one: the MainWindow-construction logic actually lives in <c>InitializeAppAsync</c> (fired
/// fire-and-forget from <c>OnFrameworkInitializationCompleted</c>), which needs a real Avalonia
/// <c>IClassicDesktopStyleApplicationLifetime</c> and a full DI graph this test project doesn't
/// build.
/// </remarks>
public class MinimizedStartupDefersMainWindowTests
{
    [Fact]
    public void AMinimizedStartDoesNotConstructMainWindow()
    {
        var body = OnFrameworkInitializationCompletedSource();

        var startMinimizedIndex = body.IndexOf(
            "bool startMinimized = desktop.Args", System.StringComparison.Ordinal);
        startMinimizedIndex.Should().BeGreaterThanOrEqualTo(0, "the startMinimized check must still exist");

        var afterCheck = body[startMinimizedIndex..];
        var constructIndex = afterCheck.IndexOf(
            "new MainWindow { DataContext = viewModel }", System.StringComparison.Ordinal);
        var showIndex = afterCheck.IndexOf("desktop.MainWindow.Show();", System.StringComparison.Ordinal);

        constructIndex.Should().BeGreaterThanOrEqualTo(0, "the non-minimized path must still construct MainWindow");
        showIndex.Should().BeGreaterThanOrEqualTo(0, "the non-minimized path must still show it");

        // Both the construction and the Show() call must be gated behind "not minimized" - proven
        // here by requiring them to appear textually inside an `if (!startMinimized)` guard rather
        // than unconditionally, matching this file's actual shape.
        afterCheck.Should().MatchRegex(
            @"if\s*\(\s*!startMinimized\s*\)\s*\{[^}]*new MainWindow\s*\{\s*DataContext\s*=\s*viewModel\s*\}[^}]*desktop\.MainWindow\.Show\(\);[^}]*\}",
            "MainWindow construction AND Show() must both be gated behind !startMinimized, not run unconditionally");
    }

    [Fact]
    public void BringMainWindowToFrontStillLazilyConstructsMainWindow()
    {
        // Pins the fallback this fix relies on: whatever surfaces the window later (tray "Show", the
        // Desktop-route consent dialog per RG:1032-1049) must still work when a minimized start left
        // MainWindow null.
        var source = AppSource();

        source.Should().MatchRegex(
            @"desktop\.MainWindow\s*\?\?=\s*new MainWindow\s*\{\s*DataContext\s*=\s*Services\.GetRequiredService<ShellViewModel>\(\)\s*\};",
            "BringMainWindowToFront must still lazily construct MainWindow when it is null - this is " +
            "the only reason deferring construction at startup is safe");
    }

    [Fact]
    public void FileConsentDialogCallsBringMainWindowToFrontBeforeReadingMainWindow()
    {
        // Round-1 review HIGH: the original code checked `desktop.MainWindow is { } owner` BEFORE
        // calling BringMainWindowToFront(), so a minimized start (MainWindow now null) fell straight
        // to the ownerless branch - exactly the "prompt nobody could see" failure RG:1032-1049 exists
        // to prevent, reached through a path the guard's own text didn't anticipate.
        var body = ExtractMethod(AppSource(), "ShowFileConsentDialogAsync");

        var bringToFrontIndex = body.IndexOf("BringMainWindowToFront();", System.StringComparison.Ordinal);
        var readMainWindowIndex = body.IndexOf(
            "MaterialDialogs.FileConsentAsync(desktop.MainWindow, vm)", System.StringComparison.Ordinal);

        bringToFrontIndex.Should().BeGreaterThanOrEqualTo(0, "the helper call must still exist");
        readMainWindowIndex.Should().BeGreaterThanOrEqualTo(0, "MainWindow must still be read as the dialog's owner");
        bringToFrontIndex.Should().BeLessThan(readMainWindowIndex,
            "BringMainWindowToFront() must run BEFORE MainWindow is read, so a deferred (null) " +
            "window is guaranteed to exist by the time it's used as the dialog's owner");

        // Round-2 review LOW: the two index checks above pass even if someone re-adds a
        // `&& desktop.MainWindow is { } owner` gate to the outer if - that gate is exactly what
        // caused the round-1 bug, since it reads MainWindow before BringMainWindowToFront() ever
        // runs. Pin the guarding `if` itself, not just the ordering of the two calls inside it.
        var ifIndex = body.IndexOf("if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)",
            System.StringComparison.Ordinal);
        ifIndex.Should().BeGreaterThanOrEqualTo(0,
            "the guarding if must check ONLY the lifetime type, not desktop.MainWindow");
    }

    [Fact]
    public void RestorePromptLazilyBuildsMainWindowInsteadOfRequiringOneToAlreadyExist()
    {
        // Round-1 review HIGH: the original condition required `restoreDesktop.MainWindow is { }
        // mainWindow` to even ENTER this block, so a minimized start silently skipped the one-time
        // first-run restore offer permanently (profileFileWasMissing only fires once per launch).
        var body = AppSource();

        body.Should().MatchRegex(
            @"restoreDesktop\.MainWindow\s*\?\?=\s*new MainWindow\s*\{\s*DataContext\s*=\s*viewModel\s*\};",
            "the restore-prompt block must lazily BUILD MainWindow (with the same singleton " +
            "viewModel), not require one to already exist before the block can even run");
    }

    [Fact]
    public void EmbeddedPairingAttachSkipsStartingTheStandaloneTimer()
    {
        // P1-31: the standalone 2s IPC poll used to start unconditionally even when the embedded
        // pairing service was just attached and already pushes PIN state via
        // PinDisplayed/PinCleared events. Pin that the poll start (and its priming refresh) are
        // now gated on whether embedded pairing was attached this run.
        var body = OnFrameworkInitializationCompletedSource();

        var flagIndex = body.IndexOf("var embeddedPairingAttached = false;", System.StringComparison.Ordinal);
        var setIndex = body.IndexOf("embeddedPairingAttached = true;", System.StringComparison.Ordinal);
        flagIndex.Should().BeGreaterThanOrEqualTo(0, "the attach flag must still exist");
        setIndex.Should().BeGreaterThanOrEqualTo(0, "successfully attaching embedded pairing must still set the flag");

        body.Should().MatchRegex(
            @"if\s*\(!embeddedPairingAttached\)\s*\{[^}]*RefreshStandalonePairingPinAsync\(\)[^}]*StartStandalonePairingPinPolling\(\)[^}]*\}",
            "the standalone poll's priming refresh and StartStandalonePairingPinPolling() must both " +
            "be gated behind !embeddedPairingAttached, not run unconditionally");

        // AttachStandalonePairingPinQueryService itself must stay unconditional - it's the fallback
        // wiring (CanRevealPairingPin) for a host with no embedded IPairingService.
        var attachIndex = body.IndexOf(
            "AttachStandalonePairingPinQueryService(pairingPinQueryService);", System.StringComparison.Ordinal);
        var gateIndex = body.IndexOf("if (!embeddedPairingAttached)", System.StringComparison.Ordinal);
        attachIndex.Should().BeGreaterThanOrEqualTo(0);
        gateIndex.Should().BeGreaterThanOrEqualTo(0);
        attachIndex.Should().BeLessThan(gateIndex,
            "AttachStandalonePairingPinQueryService must run before the embedded-pairing gate, not inside it");
    }

    /// <summary>
    /// Everything from the method's opening brace to the matching close at class indent (four
    /// spaces). Same heuristic <c>WallpaperLoadRaceTests</c> and <c>StartupViewArgumentTests</c>'
    /// siblings use, for the same reason.
    /// </summary>
    private static string OnFrameworkInitializationCompletedSource()
    {
        // OnFrameworkInitializationCompleted itself just fires `_ = InitializeAppAsync();` and
        // returns - the MainWindow construction logic this test pins actually lives in that method.
        var source = AppSource();
        var match = Regex.Match(
            source,
            @"InitializeAppAsync\s*\([^)]*\)\s*\{.*?\n    \}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue(
            "InitializeAppAsync moved, was renamed, or changed shape — update this test");
        return match.Value;
    }

    /// <summary>
    /// Everything from a method's opening brace to the matching close at class indent (four
    /// spaces). Same heuristic used throughout this file's other extractions.
    /// </summary>
    private static string ExtractMethod(string source, string methodName)
    {
        var match = Regex.Match(source, $@"{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{.*?\n    \}}",
            RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{methodName} moved, was renamed, or changed shape — update this test");
        return match.Value;
    }

    private static string AppSource([CallerFilePath] string f = "")
        => File.ReadAllText(Path.Combine(RepoRoot(f), "remex.desktop", "App.axaml.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
