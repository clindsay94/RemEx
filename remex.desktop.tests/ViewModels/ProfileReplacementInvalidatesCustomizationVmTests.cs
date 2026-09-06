using System;
using System.IO;
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
/// </remarks>
public sealed class ProfileReplacementInvalidatesCustomizationVmTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ThemeService _theme;
    private readonly DashboardLayoutService _layoutService;
    private readonly ShellViewModel _shell;

    public ProfileReplacementInvalidatesCustomizationVmTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("remex-waqb4-").FullName;
        _theme = new ThemeService { PostToUiThread = action => action() };
        _layoutService = new DashboardLayoutService(Path.Combine(_tempDir, "dashboard_layout.json"), _theme);
        _layoutService.LoadAsync().GetAwaiter().GetResult();

        _shell = new ShellViewModel(
            _layoutService,
            _theme,
            new HardwareThemeService(_theme),
            new ConnectionViewModel(),
            new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void ImportingASavefileRebuildsTheCachedCustomizationVm()
    {
        var original = _shell.CustomizationVm;
        original.Should().NotBeNull();
        var importedCornerRadius = original!.CornerRadius + 7;

        ImportProfileWithCornerRadius(importedCornerRadius);

        var afterImport = _shell.CustomizationVm;
        afterImport.Should().NotBeSameAs(original,
            "the stale snapshot must be dropped on a profile replacement, not kept alive across the import");
        afterImport!.CornerRadius.Should().Be(importedCornerRadius,
            "the rebuilt view model has to read the imported profile, not the one it was built against before the import");
    }

    [Fact]
    public void NudgingAnUnrelatedSliderAfterAnImportPersistsTheImportedValue_NotTheStaleOne()
    {
        // CornerRadius is the field the user never touches in this test — only the import changes
        // it. GlowStrength is the one nudged, and its own OnGlowStrengthChanged calls ApplyAndSave
        // directly with no snapping logic to complicate the assertion.
        var importedCornerRadius = _shell.CustomizationVm!.CornerRadius + 7;
        ImportProfileWithCornerRadius(importedCornerRadius);

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

    private void ImportProfileWithCornerRadius(double cornerRadius)
    {
        var imported = _layoutService.CurrentProfile with
        {
            Customization = _layoutService.CurrentProfile.Customization with { CornerRadius = cornerRadius },
        };

        // The real savefile-import path (RemexSavefileService.ImportDashboardLayoutAsync): a direct
        // SaveAsync followed by the LoadAsync that reads it back and becomes the new CurrentProfile.
        _layoutService.SaveAsync(imported).GetAwaiter().GetResult();
        _layoutService.LoadAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _shell.Dispose();
        _layoutService.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
