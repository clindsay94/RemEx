using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-waqb4: <c>ShellViewModel</c> caches <c>CustomizationViewModel</c> with a constructor-time
/// <c>??=</c> and never rebuilds it, and <c>CustomizationViewModel</c> snapshots every field off
/// <c>CurrentProfile.Customization</c> once, in its own constructor. A savefile import replaces
/// <c>DashboardLayoutService.CurrentProfile</c> wholesale and repaints the app, but the cached view
/// model kept none of that — so the very next slider nudge rebuilds a
/// <c>CustomizationSettings</c> from the stale snapshot and writes it back over the import.
/// </summary>
/// <remarks>
/// <para>
/// THIS IS THE ONE TEST CLASS IN THE SUITE THAT CONSTRUCTS <c>ShellViewModel</c> DIRECTLY. Every
/// other test touching this class reads the wiring off the source instead (see
/// <c>ShellPresencePulseTests</c>), because the full DI graph is otherwise painful to assemble — but
/// the bug here is specifically in how two live instances (<c>ShellViewModel</c> and its cached
/// <c>CustomizationViewModel</c>) interact across a profile replacement, and a source scan cannot see
/// an object identity change. Nothing here needs the Avalonia dispatcher pumped: construction is
/// synchronous, <c>ThemeService.PostToUiThread</c> is overridden to run inline the way
/// <c>HardwareAccentInjectionTests</c> already does, and the default profile's background material
/// is not "Wallpaper", so <c>RefreshWallpaperBackdrop</c> takes its early-return branch and never
/// touches a real bitmap decode.
/// </para>
/// <para>
/// <c>IAsyncLifetime</c>, NOT A BLOCKING CALL IN THE CONSTRUCTOR (RemEx-7cq0 /
/// <c>NoBlockingWaitsInTestsTests</c>). A constructor cannot await
/// <see cref="DashboardLayoutService.LoadAsync"/> or the savefile-import round trip, and
/// <c>.GetAwaiter().GetResult()</c> there blocks a pool thread for the class's whole setup — banned
/// repo-wide in test code, which has no P/Invoke-boundary excuse for it. <c>InitializeAsync</c> is
/// the async equivalent of the constructor here, same as <c>PairingHandlerTests</c> already uses it.
/// </para>
/// <para>
/// NO EXPLICIT QUIESCING OF <c>ShellViewModel</c>'s BACKGROUND CANVAS INIT (review, HIGH). The
/// constructor fires <c>_canvasViewModel.InitializeAsync()</c> without awaiting it, and that path
/// calls <c>DashboardLayoutService.LoadAsync()</c> on its own — a first version of this suite raced
/// that background read against the negative-control test below, since at the time EVERY load raised
/// <c>ProfileReplaced</c>. That is no longer possible: <c>ProfileReplaced</c> is now raised only by
/// <see cref="DashboardLayoutService.ReloadAsync"/>, and the canvas init calls the plain,
/// non-replacing <see cref="DashboardLayoutService.LoadAsync"/> overload — so there is nothing left
/// for it to race against. This suite still only imports via <c>ReloadAsync</c> (through
/// <see cref="ImportProfileWithCornerRadiusAsync"/>), matching the real savefile-import path.
/// </para>
/// <para>
/// <c>ProfileReplacedDispatch</c> IS SET TO RUN INLINE (review, HIGH). Production marshals the
/// ProfileReplaced handler onto the UI thread via <c>Dispatcher.UIThread.CheckAccess() ? run() :
/// Dispatcher.UIThread.Post(run)</c> — correct, because the autosnapshot timer and a manual export
/// can still call the non-replacing <c>LoadAsync</c> from a pool thread even though neither raises
/// this event any more. But this assembly has no <c>Avalonia.Headless</c> reference
/// (<c>DispatcherPostedWorkTests</c>), so nothing ever drains a real <c>Post</c> — and
/// <c>HardwareThemeService</c>'s <c>DispatcherTimer</c>, constructed a few lines above
/// <see cref="ShellViewModel"/> here, can bind "the" UI thread to a different pool thread than the
/// one this test's own awaited <c>ReloadAsync</c> resumes on, making <c>CheckAccess()</c> read false
/// and strand the real callback in a queue nothing pumps — measured directly, not assumed: an
/// earlier version of this test hung exactly that way. Substituting the seam is the same fix
/// <c>CanvasDashboardViewModel.Dispatch</c> already uses for the identical problem.
/// <see cref="TheDefaultProfileReplacedDispatchReachesTheRealDispatcher"/> is the anti-vacuity check.
/// </para>
/// </remarks>
public sealed class ProfileReplacementInvalidatesCustomizationVmTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private ShellViewModel _shell = null!;

    public ProfileReplacementInvalidatesCustomizationVmTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-waqb4-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
    }

    public async Task InitializeAsync()
    {
        await _layoutService.LoadAsync();

        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new HardwareThemeService(_theme),
            new ConnectionViewModel(),
            new ServiceCollection().BuildServiceProvider());

        // Run the ProfileReplaced handler inline (see ProfileReplacedDispatch's own remarks): this
        // assembly has no Avalonia.Headless reference, so nothing drains a real Dispatcher.UIThread
        // Post, and HardwareThemeService's DispatcherTimer above can bind "the" UI thread to a
        // different pool thread than the one a later awaited ReloadAsync resumes on.
        _shell.ProfileReplacedDispatch = run => run();
    }

    [Fact]
    public async Task ImportingASavefileRebuildsTheCachedCustomizationVm()
    {
        var original = _shell.CustomizationVm;
        original.Should().NotBeNull();
        var importedCornerRadius = original!.CornerRadius + 7;

        await ImportProfileWithCornerRadiusAsync(importedCornerRadius);

        var afterImport = _shell.CustomizationVm;
        afterImport.Should().NotBeSameAs(original,
            "the stale snapshot must be dropped on a profile replacement, not kept alive across the import");
        afterImport!.CornerRadius.Should().Be(importedCornerRadius,
            "the rebuilt view model has to read the imported profile, not the one it was built against before the import");
    }

    [Fact]
    public async Task NudgingAnUnrelatedSliderAfterAnImportPersistsTheImportedValue_NotTheStaleOne()
    {
        // CornerRadius is the field the user never touches in this test — only the import changes
        // it. GlowStrength is the one nudged, and its own OnGlowStrengthChanged calls ApplyAndSave
        // directly with no snapping logic to complicate the assertion.
        var importedCornerRadius = _shell.CustomizationVm!.CornerRadius + 7;
        await ImportProfileWithCornerRadiusAsync(importedCornerRadius);

        _shell.CustomizationVm!.GlowStrength += 0.1;

        // ApplyAndSave rebuilds the WHOLE CustomizationSettings record from the view model's own
        // fields, CornerRadius included, even though only GlowStrength was touched. If the view
        // model backing that nudge is still the pre-import instance, CornerRadius comes off its
        // stale constructor-time snapshot and silently reverts the import the moment anything is
        // saved.
        _layoutService.CurrentProfile.Customization.CornerRadius.Should().Be(importedCornerRadius,
            "a save triggered after the import must carry the imported CornerRadius forward, not " +
            "revert it to the pre-import snapshot just because a different slider moved");
    }

    [Fact]
    public void AnOrdinaryApplyAndSaveDoesNotRaiseProfileReplacedOrRebuildTheCachedVm()
    {
        var replacedCount = 0;
        _layoutService.ProfileReplaced += () => replacedCount++;
        var original = _shell.CustomizationVm;

        original!.GlowStrength += 0.1; // ordinary slider nudge -> ApplyAndSave -> RequestSave

        replacedCount.Should().Be(0,
            "RequestSave hands in the very profile the view model itself just built, and must not " +
            "raise ProfileReplaced over its own write");
        _shell.CustomizationVm.Should().BeSameAs(original,
            "an ordinary save must not drop the cached view model, or the Personalize sheet would " +
            "reset under the user's hand on every edit");
    }

    [Fact]
    public async Task TheExportAndAutosnapshotReadPathDoesNotRaiseProfileReplaced()
    {
        // The exact overload RemexSavefileService.BuildSavefileAsync calls on every manual export and
        // every 30-second autosnapshot timer tick — a read nobody asked to replace anything with.
        var replacedCount = 0;
        _layoutService.ProfileReplaced += () => replacedCount++;
        var original = _shell.CustomizationVm;

        await _layoutService.LoadAsync();

        replacedCount.Should().Be(0,
            "LoadAsync is a plain read - only ReloadAsync may raise ProfileReplaced, or a background " +
            "export/autosnapshot tick would reset a bound, open Personalize sheet from a pool thread");
        _shell.CustomizationVm.Should().BeSameAs(original,
            "a read that must not raise ProfileReplaced must also not rebuild the cached view model");
    }

    [Fact]
    public void TheDefaultProfileReplacedDispatchReachesTheRealDispatcher()
    {
        // ANTI-VACUITY FOR InitializeAsync's OVERRIDE ABOVE. Substituting ProfileReplacedDispatch
        // proves the handler routes through it; it says nothing about where the DEFAULT goes when no
        // test touches it. If the default were quietly changed to run inline, this test's own
        // override would still pass while the real app's autosnapshot/export path stopped checking
        // the UI thread at all before touching bound state — the exact silent-failure shape
        // DispatcherPostedWorkTests was written about for CanvasDashboardViewModel.Dispatch.
        //
        // NOT INVOKED, DELIBERATELY — same reason CanvasDashboardViewModel's own default is never
        // called in DispatcherPostedWorkTests: doing so would touch Dispatcher.UIThread and bind it
        // to this test's thread, the exact accidental binding ProfileReplacedDispatch exists to keep
        // out of a test that does not ask for it. Read from source instead.
        var source = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ShellViewModel.cs")),
            @"//.*$", string.Empty, RegexOptions.Multiline);

        source.Should().MatchRegex(
            @"internal Action<Action> ProfileReplacedDispatch\s*\{\s*get;\s*set;\s*\}\s*=\s*run\s*=>\s*" +
            @"\{\s*if\s*\(Dispatcher\.UIThread\.CheckAccess\(\)\)\s*run\(\);\s*else\s*Dispatcher\.UIThread\.Post\(run\);\s*\};",
            "the default has to check the UI thread and post otherwise. A default that runs inline " +
            "satisfies every test in this file while the real autosnapshot/export path stops " +
            "reaching the UI thread before touching bound Personalize-sheet state");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private async Task ImportProfileWithCornerRadiusAsync(double cornerRadius)
    {
        var imported = _layoutService.CurrentProfile with
        {
            Customization = _layoutService.CurrentProfile.Customization with { CornerRadius = cornerRadius },
        };

        // The real savefile-import path (RemexSavefileService.ImportDashboardLayoutAsync): SaveAsync
        // followed by the ReloadAsync that reads it back, becomes the new CurrentProfile, and raises
        // ProfileReplaced.
        await _layoutService.SaveAsync(imported);
        await _layoutService.ReloadAsync();
    }

    public Task DisposeAsync()
    {
        _shell.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }
}
