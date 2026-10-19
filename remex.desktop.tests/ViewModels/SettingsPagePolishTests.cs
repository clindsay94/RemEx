using System.Runtime.CompilerServices;
using Avalonia.Platform.Storage;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Settings page polish (PC-6): the Language picker and Personalize shortcut, the import confirmation
/// and the "Open automatic backups" button.
/// </summary>
public sealed class SettingsPagePolishTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
    }

    private (SettingsViewModel Vm, RemexSavefileService Savefile) NewSettings(ShellViewModel? shell = null)
    {
        var layout = new DashboardLayoutService(new ThemeService());
        _disposables.Add(layout);
        var savefile = new RemexSavefileService(
            layout, Mock.Of<ILauncherStorageService>(), new FileTransferRootSettingsService());
        _disposables.Add(savefile);

        var vm = new SettingsViewModel(
            null!, new ConnectionViewModel(), shell!, null!, savefile,
            new SensorAlertStore(), new SensorAlertTracker(), null!);
        return (vm, savefile);
    }

    // -- Personalize shortcut -----------------------------------------------------------------

    [Fact]
    public async Task OpenPersonalize_LeavesTheShellWithThePersonalizeSheetOpen()
    {
        var theme = new ThemeService { PostToUiThread = action => action() };
        var layout = new DashboardLayoutService(
            Path.Combine(Directory.CreateTempSubdirectory("remex-pc6-").FullName, "dashboard_layout.json"), theme);
        _disposables.Add(layout);
        await layout.LoadAsync();
        var savefile = new RemexSavefileService(
            layout, Mock.Of<ILauncherStorageService>(), new FileTransferRootSettingsService());
        _disposables.Add(savefile);

        var shell = new ShellViewModel(
            layout,
            theme,
            new ConnectionViewModel(),
            new ServiceCollection().AddLogging()
                .AddSingleton<SensorAlertStore>()
                .AddSingleton<SensorAlertTracker>()
                .AddSingleton(savefile)
                .BuildServiceProvider());
        _disposables.Add(shell);
        shell.ProfileReplacedDispatch = run => run();
        shell.DiagnosticsLogDispatch = run => run();

        shell.NavigateToSettings();
        shell.IsSettingsPanelOpen.Should().BeFalse("the Settings page is a page, not the side sheet");
        var settings = shell.SettingsVm;
        settings.Should().NotBeNull();

        settings!.OpenPersonalizeCommand.Execute(null);

        shell.IsSettingsPanelOpen.Should().BeTrue();
    }

    // -- Import confirmation ------------------------------------------------------------------

    private static (Func<FilePickerOpenOptions, Task<IReadOnlyList<IStorageFile>>> Picker, Func<int> OpenCount, Func<int> PickCount)
        StubPicker()
    {
        var opened = 0;
        var picked = 0;
        var file = new Mock<IStorageFile>();
        file.Setup(f => f.OpenReadAsync()).Returns(() =>
        {
            opened++;
            return Task.FromResult<Stream>(new MemoryStream(Array.Empty<byte>()));
        });

        return (_ =>
        {
            picked++;
            return Task.FromResult<IReadOnlyList<IStorageFile>>(new[] { file.Object });
        }, () => opened, () => picked);
    }

    [Fact]
    public async Task Import_WithNoDialogWired_DeclinesAndNeverOpensTheFile()
    {
        var (vm, _) = NewSettings();
        var (picker, opened, picked) = StubPicker();
        vm.PickOpenFileAsync = picker;

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        picked().Should().Be(1, "anti-vacuity: the picker ran, so the guard is what stopped the import");
        opened().Should().Be(0, "an unwired ViewModel must not replace the user's settings unconfirmed");
    }

    [Fact]
    public async Task Import_WhenUserDeclines_NeverOpensTheFile()
    {
        var (vm, _) = NewSettings();
        var (picker, opened, picked) = StubPicker();
        vm.PickOpenFileAsync = picker;
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        picked().Should().Be(1);
        opened().Should().Be(0);
    }

    [Fact]
    public async Task Import_WhenUserConfirms_ReadsTheChosenFile_AndTheDialogGetsResolvedText()
    {
        var (vm, _) = NewSettings();
        var (picker, opened, _) = StubPicker();
        vm.PickOpenFileAsync = picker;
        string? title = null, message = null, button = null;
        vm.OnConfirmationRequested = (t, m, b) =>
        {
            (title, message, button) = (t, m, b);
            return Task.FromResult(true);
        };

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        opened().Should().Be(1, "a confirmed import must go on to read the file");
        title.Should().NotBeNullOrWhiteSpace().And.NotBe("Confirm_ImportSettings_Title");
        message.Should().NotBeNullOrWhiteSpace().And.NotBe("Confirm_ImportSettings_Message");
        button.Should().NotBeNullOrWhiteSpace().And.NotBe("Confirm_ImportSettings_Btn");
    }

    [Fact]
    public async Task Import_WhenThePickerIsCancelled_ShowsNoDialog()
    {
        var (vm, _) = NewSettings();
        vm.PickOpenFileAsync = _ => Task.FromResult<IReadOnlyList<IStorageFile>>(Array.Empty<IStorageFile>());
        var asked = false;
        vm.OnConfirmationRequested = (_, _, _) =>
        {
            asked = true;
            return Task.FromResult(true);
        };

        await vm.ImportSettingsCommand.ExecuteAsync(null);

        asked.Should().BeFalse("there is nothing to confirm until a file has been chosen");
    }

    // -- Open automatic backups ---------------------------------------------------------------

    [Fact]
    public void OpenBackupsFolder_CreatesTheFolderAndOpensExactlyThatPath()
    {
        var (vm, savefile) = NewSettings();
        RemexDataPaths.HostStateDirectoryOverride.Should().NotBeNull("anti-vacuity: the test redirect must be active");
        savefile.BackupsDirectory.Should().StartWith(RemexDataPaths.HostStateDirectoryOverride!);
        if (Directory.Exists(savefile.BackupsDirectory))
            Directory.Delete(savefile.BackupsDirectory, recursive: true);

        var opened = new List<string>();
        vm.OpenFolder = path =>
        {
            opened.Add(path);
            return true;
        };

        vm.OpenBackupsFolderCommand.Execute(null);

        Directory.Exists(savefile.BackupsDirectory).Should().BeTrue();
        opened.Should().Equal(savefile.BackupsDirectory);
    }

    // -- View wiring --------------------------------------------------------------------------

    private static string SettingsViewSource([CallerFilePath] string thisFile = "")
        => File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "remex.desktop", "Views", "SettingsView.axaml"));

    [Fact]
    public void SettingsView_HasTheLanguagePickerAndBothNewButtons()
    {
        var xaml = SettingsViewSource();

        xaml.Should().Contain("ItemsSource=\"{Binding AvailableLanguages}\"");
        xaml.Should().Contain("SelectedValue=\"{Binding Language}\"");
        xaml.Should().Contain("Command=\"{Binding OpenPersonalizeCommand}\"");
        xaml.Should().Contain("Command=\"{Binding OpenBackupsFolderCommand}\"");
        typeof(SettingsViewModel).GetProperty("OpenPersonalizeCommand").Should().NotBeNull();
        typeof(SettingsViewModel).GetProperty("OpenBackupsFolderCommand").Should().NotBeNull();
        typeof(SettingsViewModel).GetProperty("AvailableLanguages").Should().NotBeNull();
        typeof(SettingsViewModel).GetProperty("Language").Should().NotBeNull();
    }
}
