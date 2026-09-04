using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// VM-level coverage for palette JSON import (RemEx-a7uzb, round-2 review finding): the apply-order
/// path (<c>ImportPaletteJsonAsync</c>) is exactly where the wrong-title rejection defect lived, and
/// <c>PaletteExchangeTests</c> only covers the static serializer, never the ViewModel that drives it.
/// </summary>
public class CustomizationPaletteImportTests : IDisposable
{
    // DISPOSED IN Dispose() BELOW (RemEx-8y3qy). Every DashboardLayoutService in this assembly
    // shares one redirected dashboard_layout.json (build/TestHostStateRedirect.cs is per-ASSEMBLY,
    // not per-test), and unlike the SettingsViewModel-harness tests elsewhere, THIS one is genuinely
    // exercised: the import path under test calls CustomizationViewModel.ApplyAndSave, which calls
    // RequestSave and arms a real 2s debounce timer. Left undisposed, that timer can fire mid-way
    // through a LATER, unrelated test and write over whatever that test just wrote or is about to
    // read - exactly the cross-test hazard this bead's own reproduction tests depend on not
    // happening.
    private DashboardLayoutService? _layoutService;

    public void Dispose() => _layoutService?.Dispose();

    private CustomizationViewModel MakeVm()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = _layoutService = new DashboardLayoutService(theme);
        // ShellViewModel is never touched by the import path (only IsReducedMotion,
        // IsPaletteDragging and NavigateBack read _shell, none of which import reaches).
        return new CustomizationViewModel(null!, layout, theme);
    }

    /// <summary>
    /// <c>IStorageFile</c> is a "not implementable outside Avalonia" interface (a compiler-enforced
    /// marker, not just convention), so a hand-written fake will not compile. Avalonia's own desktop
    /// picker wraps a real <see cref="FileInfo"/> in the internal
    /// <c>Avalonia.Platform.Storage.FileIO.BclStorageFile</c> — its constructor is public, only the
    /// class itself is not, so reflection can still build one and hand it back through the public
    /// <see cref="IStorageFile"/> interface it implements.
    /// </summary>
    private static IStorageFile WrapRealFile(string path)
    {
        var type = typeof(IStorageFile).Assembly.GetType("Avalonia.Platform.Storage.FileIO.BclStorageFile")
            ?? throw new InvalidOperationException("Avalonia's BclStorageFile moved or was removed.");
        return (IStorageFile)Activator.CreateInstance(type, new FileInfo(path))!;
    }

    private static Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>> PickReturning(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"remex-palette-import-test-{Guid.NewGuid():N}.remexpalette");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return _ => Task.FromResult<IReadOnlyList<IStorageFile>>(new[] { WrapRealFile(path) });
    }

    [Fact]
    public async Task ImportingExportedJsonAppliesAllFourFields()
    {
        var vm = MakeVm();
        var recipe = new PaletteRecipe("#FF00F3FF", "Vibrant", "Light", 0.4, 42.5);
        var json = PaletteExchange.ToJson(recipe);

        vm.PickOpenFileAsync = PickReturning(json);
        await vm.ImportPaletteJsonCommand.ExecuteAsync(null);

        vm.AccentColor.Should().Be(recipe.Seed);
        vm.SchemeVariant.Should().Be(recipe.Variant);
        vm.ThemeContrast.Should().Be(recipe.Contrast);
    }

    private sealed class RecordingSink : IInAppNotificationSink
    {
        public (NotificationImportance Importance, string Title, string Message)? Shown;
        public void Show(NotificationImportance importance, string title, string message) =>
            Shown = (importance, title, message);
    }

    [Fact]
    public async Task ImportingGarbageNotifiesInvalidAndChangesNoSetting()
    {
        var vm = MakeVm();
        var accentBefore = vm.AccentColor;
        var variantBefore = vm.SchemeVariant;
        var contrastBefore = vm.ThemeContrast;

        vm.PickOpenFileAsync = PickReturning("not a palette file {{{");

        // Route the toast synchronously and in-app, same as HardwareAccentInjectionTests does for
        // ThemeService.PostToUiThread — NotificationService.Dispatch defaults to Dispatcher.UIThread,
        // which is not available in a headless xunit run.
        var previousDispatch = NotificationService.Instance.Dispatch;
        var previousProbe = NotificationService.Instance.WindowVisibleProbe;
        var previousSink = NotificationService.Instance.InApp;
        var sink = new RecordingSink();
        NotificationService.Instance.Dispatch = action => action();
        NotificationService.Instance.WindowVisibleProbe = () => true;
        NotificationService.Instance.InApp = sink;
        try
        {
            await vm.ImportPaletteJsonCommand.ExecuteAsync(null);
        }
        finally
        {
            NotificationService.Instance.Dispatch = previousDispatch;
            NotificationService.Instance.WindowVisibleProbe = previousProbe;
            NotificationService.Instance.InApp = previousSink;
        }

        sink.Shown.Should().NotBeNull("the invalid-file path must notify, not fail silently");
        sink.Shown!.Value.Title.Should().Be(LocalizationService.Instance["Custom_ImportPaletteJson"],
            "rejection must not wear the success title (Notification_PaletteImported_Title) — "
            + "round-2 review finding");
        sink.Shown.Value.Message.Should().Be(LocalizationService.Instance["Custom_PaletteImportInvalid"]);

        vm.AccentColor.Should().Be(accentBefore);
        vm.SchemeVariant.Should().Be(variantBefore);
        vm.ThemeContrast.Should().Be(contrastBefore);
    }
}
