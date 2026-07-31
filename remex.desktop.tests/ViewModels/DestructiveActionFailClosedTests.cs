using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Remex.Core.Logging;
using Remex.Core.Models;
using Remex.Core.Services;
using Remex.Desktop.Services;
using Remex.Desktop.Services.Backup;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Pins the fail-CLOSED property that makes the confirmation pattern safe.
/// </summary>
/// <remarks>
/// Every destructive action in this app follows the same shape: the ViewModel owns no UI, so it
/// exposes <c>Func&lt;string, string, string, Task&lt;bool&gt;&gt;? OnConfirmationRequested</c> and
/// the View supplies a dialog. The safety of that arrangement rests entirely on what happens when
/// the delegate is missing — an unwired ViewModel, or a View whose window is not visible, must
/// DECLINE the destructive action rather than perform it unconfirmed.
/// <para>
/// Nothing tested that. RemEx-6p1f guarded five destructive actions across four ViewModels and
/// added no test; RemEx-07jx, which established the pattern, added none either. The failure
/// direction is the dangerous one: get the guard wrong and the action runs with no prompt and
/// nothing reports it, which is exactly the defect RemEx-5vcb later found still live on the Canvas
/// "Reboot to UEFI" button. (RemEx-d5d8.)
/// </para>
/// <para>
/// <see cref="DiagnosticLogsViewModel"/> is the cheapest site to exercise: its constructor never
/// dereferences the shell it is handed, its destructive action clears in-memory state that is
/// directly observable, and it is <see cref="IDisposable"/> so the static log subscription does not
/// outlive the test. The property under test is the pattern's, not this ViewModel's.
/// </para>
/// </remarks>
public class DestructiveActionFailClosedTests
{
    /// <summary>
    /// Builds a ViewModel holding one log entry. Entries are appended BEFORE construction on
    /// purpose: appending afterwards raises <c>LogAdded</c>, whose handler posts to Avalonia's UI
    /// dispatcher, which does not exist in a unit test.
    /// </summary>
    private static DiagnosticLogsViewModel CreateViewModelWithOneEntry()
    {
        InMemoryLogSink.Clear();
        InMemoryLogSink.Append(LogLevel.Warning, "Test", "an entry that must survive a declined clear", null);

        // The shell is stored and never dereferenced during construction, so the test needs no
        // ShellViewModel — building one would drag in the whole DI graph for no added coverage.
        var vm = new DiagnosticLogsViewModel(null!);
        vm.VisibleEntries.Should().NotBeEmpty("the fixture is meaningless if there is nothing to clear");
        return vm;
    }

    [Fact]
    public async Task ClearLogs_WithNoDialogWired_DeclinesRatherThanClearing()
    {
        using var vm = CreateViewModelWithOneEntry();
        var before = vm.VisibleEntries.Count;

        // OnConfirmationRequested deliberately left null — the unwired-ViewModel case.
        await vm.ClearLogsCommand.ExecuteAsync(null);

        vm.VisibleEntries.Should().HaveCount(before,
            "an unwired ViewModel must decline a destructive action, not perform it unconfirmed");
    }

    [Fact]
    public async Task ClearLogs_WhenUserDeclines_DoesNotClear()
    {
        using var vm = CreateViewModelWithOneEntry();
        var before = vm.VisibleEntries.Count;
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.ClearLogsCommand.ExecuteAsync(null);

        vm.VisibleEntries.Should().HaveCount(before,
            "a delegate returning false must have the same effect as no delegate at all");
    }

    // The positive control. Without it the two tests above would still pass if ClearLogsAsync were
    // broken outright and cleared nothing under any circumstances.
    [Fact]
    public async Task ClearLogs_WhenUserConfirms_Clears()
    {
        using var vm = CreateViewModelWithOneEntry();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        await vm.ClearLogsCommand.ExecuteAsync(null);

        vm.VisibleEntries.Should().BeEmpty("a confirmed destructive action must actually happen");
    }

    [Fact]
    public async Task ClearLogs_PassesLocalizedConfirmationText_NotResourceKeys()
    {
        using var vm = CreateViewModelWithOneEntry();
        string? title = null;
        vm.OnConfirmationRequested = (t, _, _) =>
        {
            title = t;
            return Task.FromResult(false);
        };

        await vm.ClearLogsCommand.ExecuteAsync(null);

        // LocalizationService's indexer ends in "?? key", so a missing resource surfaces as the key
        // itself rather than throwing — the dialog would show "Confirm_ClearLogs_Title" verbatim.
        title.Should().NotBeNullOrWhiteSpace();
        title.Should().NotBe("Confirm_ClearLogs_Title",
            "the dialog must receive resolved text, not the resource key");
    }

    // ── Kill Process (RemEx-w9ui) ────────────────────────────────────────────────
    //
    // A second site, chosen because ending a process discards whatever that program had not saved
    // and there is no undo — the most destructive of the confirmed actions. TaskManagerViewModel
    // takes only a ConnectionViewModel, which has a parameterless constructor, so this needs no
    // socket and no DI graph.
    //
    // The observable is KillError. Past the guard, KillProcessAsync re-verifies that the PID it is
    // about to kill still belongs to the program the dialog named (RemEx-2s91) — and a freshly
    // built ViewModel has an empty process list, so that check always fails and sets KillError.
    // That makes "did we get past the guard" directly visible without sending anything anywhere.

    private static (TaskManagerViewModel Vm, ProcessInfo Target) CreateTaskManager() =>
            (new TaskManagerViewModel(new ConnectionViewModel()),
             new ProcessInfo { Id = 4242, Name = "notepad" });

    [Fact]
    public async Task KillProcess_WithNoDialogWired_DoesNotProceed()
    {
        var (vm, target) = CreateTaskManager();

        // KillProcessCommand is exposed as ICommand, so reach the awaitable form explicitly.
        await ((IAsyncRelayCommand)vm.KillProcessCommand).ExecuteAsync(target);

        vm.KillError.Should().BeNull(
            "an unwired ViewModel must not reach the kill path at all, so nothing past the guard " +
            "should have run");
    }

    [Fact]
    public async Task KillProcess_WhenUserDeclines_DoesNotProceed()
    {
        var (vm, target) = CreateTaskManager();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        // KillProcessCommand is exposed as ICommand, so reach the awaitable form explicitly.
        await ((IAsyncRelayCommand)vm.KillProcessCommand).ExecuteAsync(target);

        vm.KillError.Should().BeNull("declining must have the same effect as no dialog at all");
    }

    // Positive control: proves the two above are not passing simply because KillProcessAsync never
    // does anything. Confirming must get past the guard, which the PID re-verification then reports.
    [Fact]
    public async Task KillProcess_WhenUserConfirms_ProceedsPastTheGuard()
    {
        var (vm, target) = CreateTaskManager();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        // KillProcessCommand is exposed as ICommand, so reach the awaitable form explicitly.
        await ((IAsyncRelayCommand)vm.KillProcessCommand).ExecuteAsync(target);

        vm.KillError.Should().NotBeNull(
            "a confirmed kill must run the code after the guard, which reports that the target is " +
            "no longer present");
    }

    [Fact]
    public async Task KillProcess_NamesTheProcessInTheConfirmation()
    {
        var (vm, target) = CreateTaskManager();
        string? message = null;
        vm.OnConfirmationRequested = (_, m, _) =>
        {
            message = m;
            return Task.FromResult(false);
        };

        // KillProcessCommand is exposed as ICommand, so reach the awaitable form explicitly.
        await ((IAsyncRelayCommand)vm.KillProcessCommand).ExecuteAsync(target);

        // The user has to be told WHICH process, or the prompt is unanswerable — and the format
        // string resolving to its own key would be invisible without this.
        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("notepad").And.Contain("4242");
    }

    // ── Reboot to UEFI (RemEx-w9ui) ──────────────────────────────────────────────
    //
    // The highest-value of the remaining sites. Its guard is the most recently added (RemEx-5vcb)
    // and this was the site that had NO confirmation at all for however long, while every other
    // surface confirmed the identical command — so it is the one whose missing test would be least
    // likely to be noticed.
    //
    // THE BEAD'S COST SURVEY WAS WRONG ABOUT THIS, and the correction is worth more than the test:
    // it lists CanvasDashboardViewModel under "NEEDS SEAMS - constructor demands a ShellViewModel,
    // which takes five services and Guard.NotNull's all of them". The constructor demands the TYPE,
    // not a live instance: it only assigns `_shell = shell` and never dereferences it, exactly like
    // DiagnosticLogsViewModel. So null! works here for the same reason it worked there, and no seam,
    // no DI graph and no interface were needed. The same is true of AppLauncherViewModel and
    // SettingsViewModel below — all three were mis-costed.
    //
    // The observable is Connection.StatusText. Past the guard the command reaches
    // ConnectionViewModel.RestartToUefiAsync → SendPowerCommandAsync, and SendCommandAsync returns
    // ("Not connected") immediately when the socket is not open (no network, no hang), which
    // SendPowerCommandAsync then surfaces into StatusText. A sentinel is written first so the
    // assertion does not depend on StatusText's initial value.

    private const string Sentinel = "sentinel — untouched";

    private static CanvasDashboardViewModel CreateCanvas()
    {
        // layoutService and shell are stored and never dereferenced, by the constructor or by the
        // reboot path. Building either for real would drag in the whole DI graph for no coverage.
        var vm = new CanvasDashboardViewModel(new ConnectionViewModel(), null!, null!);
        vm.Connection.StatusText = Sentinel;
        return vm;
    }

    [Fact]
    public async Task RestartToUefi_WithNoDialogWired_DoesNotSendTheCommand()
    {
        var vm = CreateCanvas();

        await vm.RestartToUefiCommand.ExecuteAsync(null);

        vm.Connection.StatusText.Should().Be(Sentinel,
            "an unwired ViewModel must not reboot the PC into firmware unconfirmed — it is the " +
            "least recoverable action in the app");
    }

    [Fact]
    public async Task RestartToUefi_WhenUserDeclines_DoesNotSendTheCommand()
    {
        var vm = CreateCanvas();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.RestartToUefiCommand.ExecuteAsync(null);

        vm.Connection.StatusText.Should().Be(Sentinel,
            "declining must have the same effect as no dialog at all");
    }

    // Positive control: without it the two above would pass even if RestartToUefiAsync were broken
    // outright and never sent anything under any circumstances.
    [Fact]
    public async Task RestartToUefi_WhenUserConfirms_ProceedsPastTheGuard()
    {
        var vm = CreateCanvas();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        await vm.RestartToUefiCommand.ExecuteAsync(null);

        vm.Connection.StatusText.Should().NotBe(Sentinel,
            "a confirmed reboot must run the code after the guard, which reports the send outcome");
    }

    // ── Remove launcher card (RemEx-w9ui) ────────────────────────────────────────
    //
    // The cleanest observable of the five: past the guard the entry is removed from Launchers, an
    // ObservableCollection on the ViewModel itself — no fake connection needed to see it. Not
    // connected, so the confirmed path takes the SaveLaunchersAsync branch into the fake storage
    // rather than sending anything.

    /// <summary>
    /// Minimal <see cref="ILauncherStorageService"/>. The interface is two methods, so a fake is
    /// cheaper here than a mock and states its own behaviour.
    /// </summary>
    private sealed class FakeLauncherStorage : ILauncherStorageService
    {
        public List<AppEntry> Saved { get; private set; } = new();

        public Task<List<AppEntry>> LoadEntriesAsync() => Task.FromResult(new List<AppEntry>());

        public Task SaveEntriesAsync(IEnumerable<AppEntry> entries)
        {
            Saved = entries.ToList();
            return Task.CompletedTask;
        }
    }

    private static (AppLauncherViewModel Vm, AppEntry Target) CreateLauncher()
    {
        // The constructor kicks off LoadLaunchersAsync, which replaces Launchers wholesale — so the
        // fixture entry is added AFTER construction or it would be discarded. The fake completes
        // synchronously, so that load is already done by the time the constructor returns.
        var vm = new AppLauncherViewModel(new ConnectionViewModel(), null!, new FakeLauncherStorage());
        var target = new AppEntry(Guid.NewGuid(), "Calculator", @"C:\Windows\System32\calc.exe", "#4A3AFF", null);
        vm.Launchers.Add(target);
        return (vm, target);
    }

    [Fact]
    public async Task RemoveApp_WithNoDialogWired_KeepsTheCard()
    {
        var (vm, target) = CreateLauncher();

        await vm.RemoveAppCommand.ExecuteAsync(target);

        vm.Launchers.Should().Contain(target,
            "an unwired ViewModel must keep the card; its custom colour and icon are gone once removed");
    }

    [Fact]
    public async Task RemoveApp_WhenUserDeclines_KeepsTheCard()
    {
        var (vm, target) = CreateLauncher();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.RemoveAppCommand.ExecuteAsync(target);

        vm.Launchers.Should().Contain(target, "declining must have the same effect as no dialog at all");
    }

    // Positive control.
    [Fact]
    public async Task RemoveApp_WhenUserConfirms_RemovesTheCard()
    {
        var (vm, target) = CreateLauncher();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        await vm.RemoveAppCommand.ExecuteAsync(target);

        vm.Launchers.Should().NotContain(target, "a confirmed removal must actually happen");
    }

    [Fact]
    public async Task RemoveApp_NamesTheCardInTheConfirmation()
    {
        var (vm, target) = CreateLauncher();
        string? message = null;
        vm.OnConfirmationRequested = (_, m, _) =>
        {
            message = m;
            return Task.FromResult(false);
        };

        await vm.RemoveAppCommand.ExecuteAsync(target);

        // Confirm_RemoveApp_Format is a format string; if it resolved to its own key the user would
        // be asked to confirm deleting something the prompt never names.
        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("Calculator");
    }

    // ── Delete on the connected device (RemEx-w9ui) ──────────────────────────────
    //
    // The work here is the FIXTURE, not the construction: the delete path needs a writable root and
    // a non-empty selection before the guard is even reached, and CanDeleteRemote gates the command
    // on both. Observable is StatusText — past the guard the command sets a "Deleting…" status
    // before it ever touches the socket, so the guard is visible without a fake connection.

    private static FileTransferViewModel CreateFileTransfer()
    {
        var vm = new FileTransferViewModel(new ConnectionViewModel());
        vm.SelectedRemoteRoot = new FileSharedRoot
        {
            RootId = "documents",
            DisplayName = "Documents",
            IsWritable = true,
            CanDelete = true,
        };
        vm.SetSelectedEntries(new[] { new FileEntry { Name = "notes.txt", IsDirectory = false } });

        // Written last: the constructor starts InitializeAsync, and a sentinel set before that
        // settled could be overwritten by it rather than by the code under test.
        vm.StatusText = Sentinel;
        return vm;
    }

    [Fact]
    public async Task DeleteRemote_WithNoDialogWired_DoesNotDelete()
    {
        var vm = CreateFileTransfer();

        await vm.DeleteRemoteCommand.ExecuteAsync(null);

        vm.StatusText.Should().Be(Sentinel,
            "an unwired ViewModel must not delete anything; remote deletes are permanent");
    }

    [Fact]
    public async Task DeleteRemote_WhenUserDeclines_DoesNotDelete()
    {
        var vm = CreateFileTransfer();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.DeleteRemoteCommand.ExecuteAsync(null);

        vm.StatusText.Should().Be(Sentinel, "declining must have the same effect as no dialog at all");
    }

    // Positive control. Deliberately not awaited to completion — see below.
    [Fact]
    public async Task DeleteRemote_WhenUserConfirms_ProceedsPastTheGuard()
    {
        var vm = CreateFileTransfer();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        // Started on the thread pool, deliberately OFF xUnit's synchronization context. Awaiting
        // this command to completion means sitting out FileTransferClient's 60-second
        // ManageRequestTimeoutSeconds, because nothing ever answers the manage request — and a
        // plain fire-and-forget does not help either, since xUnit waits for outstanding async
        // operations posted to its own context before it finishes the test. Measured: 61s for this
        // one test, in a suite that otherwise runs in about a second.
        _ = Task.Run(() => vm.DeleteRemoteCommand.ExecuteAsync(null));

        // Wait for the OBSERVABLE rather than for the command. StatusText is set immediately past
        // the guard, before the socket is touched, so this polls for a change that must happen —
        // and a poll for something that must happen fails only when it does not, which is the
        // property under test. (Polling for an ABSENCE would be the flaky direction; the two
        // fail-closed cases above therefore await the command normally, which is cheap because
        // neither of them ever reaches the client.)
        var proceeded = await WaitForAsync(() => vm.StatusText != Sentinel);

        proceeded.Should().BeTrue(
            "a confirmed delete must run the code after the guard, which reports progress before " +
            "it touches the socket");
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or the timeout elapses. Used only where
    /// the work under observation cannot be awaited — see the delete positive control above.
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task DeleteRemote_NamesTheFileInTheConfirmation()
    {
        var vm = CreateFileTransfer();
        string? message = null;
        vm.OnConfirmationRequested = (_, m, _) =>
        {
            message = m;
            return Task.FromResult(false);
        };

        await vm.DeleteRemoteCommand.ExecuteAsync(null);

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().Contain("notes.txt");
    }

    // ── Restore default shared folders (RemEx-w9ui) ──────────────────────────────
    //
    // SettingsViewModel guards THREE destructive actions. Only this one is reachable from a test;
    // see the recorded reason on the other two at the bottom of this file.
    //
    // WHY _fileTransferRootSettings IS DELIBERATELY null!, and why that is the safe choice rather
    // than the lazy one: the real FileTransferRootSettingsService writes the MACHINE-WIDE shared
    // roots store under ProgramData — the same file the host reads. A positive control built on the
    // real service would reset the developer's actual shared folders every time the suite ran. With
    // it null, the confirmed path throws past the guard and the ViewModel's own catch reports it via
    // SavedStatus, which makes "did we get past the guard" observable while performing no real
    // write. That is the same shape as the Kill Process site above, where the observable is the
    // failure of a re-verification rather than a completed kill.
    private static SettingsViewModel CreateSettings()
    {
        // savefileService is the only Guard.NotNull'd dependency, so it is the only one built for
        // real; layoutService, shell and fileTransferRootSettings are stored, never dereferenced
        // during construction.
        var savefile = new RemexSavefileService(
            new DashboardLayoutService(new ThemeService()),
            new FakeLauncherStorage(),
            new FileTransferRootSettingsService(),
            Mock.Of<IDashboardProfileStorageService>());

        var vm = new SettingsViewModel(null!, new ConnectionViewModel(), null!, null!, savefile);
        vm.SavedStatus = Sentinel;
        return vm;
    }

    [Fact]
    public async Task RestoreDefaultSharedFolders_WithNoDialogWired_DoesNotRestore()
    {
        var vm = CreateSettings();

        await vm.RestoreDefaultSharedFoldersCommand.ExecuteAsync(null);

        vm.SavedStatus.Should().Be(Sentinel,
            "an unwired ViewModel must leave the folder list alone; restoring discards every folder " +
            "the user added to sharing");
    }

    [Fact]
    public async Task RestoreDefaultSharedFolders_WhenUserDeclines_DoesNotRestore()
    {
        var vm = CreateSettings();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.RestoreDefaultSharedFoldersCommand.ExecuteAsync(null);

        vm.SavedStatus.Should().Be(Sentinel, "declining must have the same effect as no dialog at all");
    }

    // Positive control.
    [Fact]
    public async Task RestoreDefaultSharedFolders_WhenUserConfirms_ProceedsPastTheGuard()
    {
        var vm = CreateSettings();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        await vm.RestoreDefaultSharedFoldersCommand.ExecuteAsync(null);

        vm.SavedStatus.Should().NotBe(Sentinel,
            "a confirmed restore must run the code after the guard, which reports the outcome");
    }

    // ── Recorded reason: the two Settings sites NOT covered (RemEx-w9ui acceptance) ──
    //
    // SettingsViewModel's other two confirmed actions — revoke device trust
    // (OnTrustRevokeRequested) and remove a shared folder (OnSharedRootRemoveRequested) — are
    // `async void` EVENT HANDLERS on FileTrustDeviceItem / FileTransferSharedRootItem, not commands.
    // A test can raise the event but cannot await the handler, so only the two fail-closed cases
    // would be deterministic: both short-circuit before any real await (`OnConfirmationRequested is
    // null`, or a delegate returning an already-completed Task.FromResult(false)), so the handler
    // runs to completion synchronously. The POSITIVE CONTROL is the one that cannot be made
    // deterministic — it awaits a real service call, so the assertion would have to poll for a
    // collection change with a timeout.
    //
    // That is the wrong trade. The bead is explicit that the positive control is what stops cases
    // 1-2 passing against an action that is broken outright, so two-thirds of the pattern plus a
    // flaky third is worth less than the honest gap recorded here. Both handlers additionally need
    // a live trust service (ResolveTrustService()) or the machine-wide shared-roots store.
    //
    // WHAT WOULD FIX IT, if this is ever worth doing: give each handler an awaitable core
    // (`internal Task RevokeTrustAsync(FileTrustDeviceItem item)`) and let the `async void` handler
    // be a one-line forwarder. That is a production change for testability, which is exactly what
    // the bead says to prefer over building the DI graph — but it is a change to a security-adjacent
    // path (trust revocation), so it wants its own bead and its own review rather than riding along
    // in a test-only one. Filed as RemEx-e1re.
}
