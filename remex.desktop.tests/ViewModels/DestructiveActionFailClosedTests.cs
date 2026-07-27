using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Core.Models;
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
}
