using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-a8du, item 3: "Open containing folder" after an export. Routes through
/// <see cref="DiagnosticLogsViewModel.FolderLauncher"/>, a replaceable delegate — the same seam
/// <c>CopyToClipboardAsync</c> and <c>PickSaveFileAsync</c> use — so no test here ever spawns a real
/// process.
/// </summary>
public class DiagnosticLogsOpenExportFolderTests
{
    // The shell is stored and never dereferenced by DiagnosticLogsViewModel's constructor or by the
    // command under test here (DestructiveActionFailClosedTests documents the same for this type).
    private static DiagnosticLogsViewModel CreateViewModel() => new(null!);

    [Fact]
    public void OpenExportFolder_WithNothingExportedYet_NeverCallsTheLauncher()
    {
        using var vm = CreateViewModel();
        var called = false;
        vm.FolderLauncher = _ => called = true;

        vm.OpenExportFolderCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void OpenExportFolder_PassesTheDirectory_NotTheFile()
    {
        using var vm = CreateViewModel();
        var exported = Path.Combine(Path.GetTempPath(), "RemEx-log-fixture.json");
        vm.LastExportedFilePath = exported;
        string? launchedWith = null;
        vm.FolderLauncher = dir => launchedWith = dir;

        vm.OpenExportFolderCommand.Execute(null);

        Assert.Equal(Path.GetDirectoryName(exported), launchedWith);
    }

    [Fact]
    public void OpenExportFolder_WhenTheLauncherThrows_RecordsItRatherThanCrashing()
    {
        // Mirrors the AboutViewModel.DownloadUpdate pattern (RemEx-43ha): a failed reveal is a
        // missing convenience, not a defect worth crashing over, but silence would mean the click
        // looked like it worked.
        using var vm = CreateViewModel();
        vm.LastExportedFilePath = Path.Combine(Path.GetTempPath(), "RemEx-log-fixture.json");
        vm.FolderLauncher = _ => throw new InvalidOperationException("no file manager");
        InMemoryLogSink.Clear();

        vm.OpenExportFolderCommand.Execute(null);

        var entries = InMemoryLogSink.GetEntries();
        Assert.Contains(entries, e => e.Level == LogLevel.Warning && e.Exception is InvalidOperationException);
    }

    [Fact]
    public void LastExportedStatusText_NamesTheFile_UsingTheSameFormatAsTheExportToast()
    {
        // The inline status row deliberately reuses Notification_LogsExported_Message rather than
        // duplicating "Saved to {0}." as a second resource key (RemEx-a8du).
        using var vm = CreateViewModel();

        vm.LastExportedFileName = "RemEx-log-20260906.json";

        Assert.NotNull(vm.LastExportedStatusText);
        Assert.Contains("RemEx-log-20260906.json", vm.LastExportedStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void LastExportedStatusText_IsNullBeforeAnythingIsExported()
    {
        using var vm = CreateViewModel();

        Assert.Null(vm.LastExportedStatusText);
    }
}
