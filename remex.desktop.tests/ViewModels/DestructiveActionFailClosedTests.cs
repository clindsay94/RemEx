using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Remex.Core.Logging;
using Remex.Core.Models;
using Remex.Core.Services.FileTransfer;
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
    // WHY _fileTransferRootSettings IS DELIBERATELY null!, and the reason has CHANGED (RemEx-rj0a).
    // It used to be that the real FileTransferRootSettingsService wrote the machine-wide shared roots
    // store under ProgramData, so a positive control built on it would reset the developer's actual
    // shared folders every run. RemEx-4u29 made the host-state redirect unconditional in every test
    // assembly, and that service resolves through RemexDataPaths, so under test it now writes into
    // the per-run directory and touches nothing real. What null! still buys is that the confirmed
    // path throws PAST the guard rather than succeeding quietly, which is what makes "did we get past
    // the guard" observable at all — the isolation was a second benefit, not the point. With
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

    // ── The two Settings sites RemEx-w9ui had to leave uncovered, now covered (RemEx-e1re) ──
    //
    // Revoke device trust and remove a shared folder are `async void` EVENT HANDLERS on
    // FileTrustDeviceItem / FileTransferSharedRootItem, not commands. A test could raise the event
    // but not await the handler, so only the two fail-closed cases were deterministic — the POSITIVE
    // CONTROL awaits a real service call and would have needed a polling timeout. Two-thirds of the
    // pattern plus a flaky third is worth less than an honest gap, so the gap was recorded here
    // instead. Each handler now forwards to an awaitable core, and the same three cases apply.

    [Fact]
    public async Task RevokeTrust_WithNoDialogWired_KeepsTheDevice()
    {
        var (vm, device) = SettingsWithTrustedDevice();

        await vm.RevokeTrustAsync(device);

        vm.TrustedDevices.Should().Contain(device,
            "an unwired ViewModel must keep the device trusted; revoking severs that phone's file "
            + "access until the user pairs and approves it again");
    }

    [Fact]
    public async Task RevokeTrust_WhenUserDeclines_KeepsTheDevice()
    {
        var (vm, device) = SettingsWithTrustedDevice();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.RevokeTrustAsync(device);

        vm.TrustedDevices.Should().Contain(device, "declining must have the same effect as no dialog at all");
    }

    // Positive control.
    [Fact]
    public async Task RevokeTrust_WhenUserConfirms_RemovesTheDevice()
    {
        var (vm, device) = SettingsWithTrustedDevice();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        await vm.RevokeTrustAsync(device);

        vm.TrustedDevices.Should().NotContain(device,
            "a confirmed revoke must run the code after the guard — without this, the two cases "
            + "above would pass against an action that does nothing at all");
    }

    [Fact]
    public async Task RevokeTrust_NamesTheDeviceInTheConfirmation()
    {
        var (vm, device) = SettingsWithTrustedDevice();
        string? body = null;
        vm.OnConfirmationRequested = (_, message, _) => { body = message; return Task.FromResult(false); };

        await vm.RevokeTrustAsync(device);

        body.Should().NotBeNull();
        // DISPLAYNAME RATHER THAN SHORTID SINCE RemEx-9me77, and the property being pinned is
        // unchanged: the user must be told WHICH device. The dialog now names it the way the rest of
        // Settings does instead of quoting a truncated client id, and with no name map in this test
        // DisplayName falls back to the full id — PairedDeviceDisplayName.Resolve's never-blank
        // contract. FileTrustDisplayNameTests covers the named case.
        body.Should().Contain(device.DisplayName,
            "the user must be told WHICH device they are about to cut off, not just that they are");
        body.Should().NotContain("Confirm_", "a resource key reaching the dialog means the lookup failed");
    }

    [Fact]
    public async Task RemoveSharedFolder_WithNoDialogWired_KeepsTheFolder()
    {
        var (vm, root) = SettingsWithSharedRoot();

        await vm.RemoveSharedRootAsync(root);

        vm.SharedRoots.Should().Contain(root,
            "an unwired ViewModel must keep the folder shared; removing revokes the phone's access "
            + "to that whole folder tree");
    }

    [Fact]
    public async Task RemoveSharedFolder_WhenUserDeclines_KeepsTheFolder()
    {
        var (vm, root) = SettingsWithSharedRoot();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(false);

        await vm.RemoveSharedRootAsync(root);

        vm.SharedRoots.Should().Contain(root, "declining must have the same effect as no dialog at all");
    }

    // Positive control.
    [Fact]
    public async Task RemoveSharedFolder_WhenUserConfirms_RemovesTheFolder()
    {
        var (vm, root) = SettingsWithSharedRoot();
        vm.OnConfirmationRequested = (_, _, _) => Task.FromResult(true);

        await vm.RemoveSharedRootAsync(root);

        vm.SharedRoots.Should().NotContain(root, "a confirmed removal must actually remove it");
    }

    [Fact]
    public async Task RemoveSharedFolder_NamesTheFolderInTheConfirmation()
    {
        var (vm, root) = SettingsWithSharedRoot();
        string? body = null;
        vm.OnConfirmationRequested = (_, message, _) => { body = message; return Task.FromResult(false); };

        await vm.RemoveSharedRootAsync(root);

        body.Should().NotBeNull();
        body.Should().Contain(root.DisplayName);
        body.Should().NotContain("Confirm_");
    }

    // ── What is STILL not covered here, and it is one line each (RemEx-e1re) ──
    //
    // Every test above calls the awaitable core directly. Nothing exercises the `async void`
    // forwarders themselves — `if (sender is X item) await CoreAsync(item);` — nor the subscription
    // that reaches them, so a wrong cast, a missing await, or a handler never wired to the item's
    // event would leave all of this green while the button did nothing.
    //
    // TRIED AND FOUND UNREACHABLE, not skipped: driving the real path needs the item to exist with
    // its subscription, and the only thing that subscribes is ReplaceTrustedDevices, reached from
    // LoadTrustedDevicesAsync — which ends in `Dispatcher.UIThread.Post(...)`. Nothing pumps the
    // Avalonia dispatcher in this assembly (there is no Avalonia.Headless reference anywhere in the
    // repo), so the posted work never runs and the collection stays empty. Subscribing by hand in a
    // test would exercise a wiring the production code does not use, which is worse than the gap.
    //
    // This is a far smaller residue than the one it replaced — that was "the entire action is
    // untestable", this is "the one-line forwarder is". Closing it needs a headless dispatcher
    // harness, which several other view-model paths would also benefit from - RemEx-r8c6.

    /// <summary>A Settings view model with one trusted device and a trust service that accepts revokes.</summary>
    /// <remarks>
    /// The fake is required, not convenient: ResolveTrustService reaches into the embedded host
    /// locator, which finds nothing in a test run, so without it every case returns at the same early
    /// guard and the positive control proves nothing.
    /// </remarks>
    private static (SettingsViewModel Vm, FileTrustDeviceItem Device) SettingsWithTrustedDevice()
    {
        var vm = CreateSettings();
        vm.FileTrustServiceForTests = new RevokeRecordingTrustService();

        var device = new FileTrustDeviceItem("client-abcdef123456", fullBrowseGranted: true, autoAcceptIncoming: false, names: null);
        vm.TrustedDevices.Add(device);
        return (vm, device);
    }

    /// <summary>A Settings view model with one shared folder in its list.</summary>
    /// <remarks>
    /// WHAT IS DELIBERATELY NOT EXERCISED: the save. CreateSettings passes a null root-settings
    /// service, so SaveSharedRootsAsync faults into its own catch and writes nothing — which is the
    /// point, because the real one writes the MACHINE-WIDE shared-roots file and no test may touch
    /// the operator's actual sharing configuration. The assertions are therefore on the collection,
    /// which is the revocation itself; persisting it is a separate concern with its own error path.
    /// </remarks>
    private static (SettingsViewModel Vm, FileTransferSharedRootItem Root) SettingsWithSharedRoot()
    {
        var vm = CreateSettings();
        var root = new FileTransferSharedRootItem("root-1", "Documents", Path.GetTempPath(), isWritable: true);

        vm.SharedRoots.Add(root);
        return (vm, root);
    }

    /// <summary>An IFileTrustService whose revoke succeeds and whose reads return nothing.</summary>
    private sealed class RevokeRecordingTrustService : IFileTrustService
    {
        /// <summary>What GetAllAsync hands back, so a test can drive the real load path.</summary>
        public List<FileTrustRecord> Records { get; } = [];

        public Task<FileTrustRecord?> GetTrustAsync(string clientId, CancellationToken ct)
            => Task.FromResult<FileTrustRecord?>(null);

        public Task<IReadOnlyList<FileTrustRecord>> GetAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FileTrustRecord>>(Records);

        public Task<bool> IsFullBrowseGrantedAsync(string clientId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> IsAutoAcceptIncomingAsync(string clientId, CancellationToken ct) => Task.FromResult(false);

        public Task SetFullBrowseGrantedAsync(string clientId, bool granted, CancellationToken ct) => Task.CompletedTask;

        public Task SetAutoAcceptIncomingAsync(string clientId, bool autoAccept, CancellationToken ct) => Task.CompletedTask;

        public Task RevokeAsync(string clientId, CancellationToken ct) => Task.CompletedTask;

        public Task<FileConsentDecision> RequestConsentAsync(string clientId, FileConsentRequest request, CancellationToken ct)
            => throw new NotSupportedException("the destructive-action tests never prompt for consent");

        public bool TryResolveRemoteConsent(string? clientId, string? consentId, bool granted, bool remember) => false;

        public void ResolveConsent(string consentId, bool granted, bool remember)
            => throw new NotSupportedException("the destructive-action tests never prompt for consent");

        // Never raised: nothing here requests consent. Present to satisfy the interface.
        public event Action<FileConsentPrompt>? ConsentRequested { add { } remove { } }
    }

}
