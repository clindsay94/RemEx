using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-a8du, item 1's remainder: the per-row context menu's "Copy message" and "Copy with stack".
/// RemEx-7xhln already covered the multi-select Copy Selected / Ctrl+C path
/// (<see cref="LogClipboardFormatTests"/>) — this is the single-row variant left open on the bead.
/// </summary>
public class DiagnosticLogsCopyEntryTests
{
    private static LogEntry Entry(string message, Exception? exception = null) =>
        new(new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc),
            exception is null ? LogLevel.Information : LogLevel.Error, "Cat", message, exception);

    // The shell is stored and never dereferenced by DiagnosticLogsViewModel's constructor or by
    // either command under test here — the same reasoning DestructiveActionFailClosedTests already
    // documented for this exact type, so null! needs no ShellViewModel DI graph.
    private static DiagnosticLogsViewModel CreateViewModel() => new(null!);

    [Fact]
    public async Task CopyEntryMessage_PutsOnlyTheBareMessageOnTheClipboard()
    {
        using var vm = CreateViewModel();
        string? clipboard = null;
        vm.CopyToClipboardAsync = text => { clipboard = text; return Task.CompletedTask; };
        var entry = Entry("something happened", new InvalidOperationException("boom"));

        await vm.CopyEntryMessageCommand.ExecuteAsync(entry);

        Assert.Equal("something happened", clipboard);
        Assert.DoesNotContain("boom", clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyEntryWithStack_MatchesWhatTheListRenders_ExceptionIncluded()
    {
        // Same "not a second format" rule LogClipboardFormatTests pins for the multi-select copy:
        // LogEntry.ToString() is what the row shows, so the single-row variant must match it exactly
        // rather than re-deriving its own text.
        using var vm = CreateViewModel();
        string? clipboard = null;
        vm.CopyToClipboardAsync = text => { clipboard = text; return Task.CompletedTask; };
        var entry = Entry("push failed", new InvalidOperationException("boom"));

        await vm.CopyEntryWithStackCommand.ExecuteAsync(entry);

        Assert.Equal(entry.ToString(), clipboard);
        Assert.Contains("boom", clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyEntryWithStack_WithNoException_IsStillJustTheRenderedLine()
    {
        using var vm = CreateViewModel();
        string? clipboard = null;
        vm.CopyToClipboardAsync = text => { clipboard = text; return Task.CompletedTask; };
        var entry = Entry("no trouble here");

        await vm.CopyEntryWithStackCommand.ExecuteAsync(entry);

        Assert.Equal(entry.ToString(), clipboard);
    }

    [Fact]
    public async Task CopyEntryCommands_WithNoEntry_TouchNeitherClipboardNorThrow()
    {
        using var vm = CreateViewModel();
        var touched = false;
        vm.CopyToClipboardAsync = _ => { touched = true; return Task.CompletedTask; };

        await vm.CopyEntryMessageCommand.ExecuteAsync(null);
        await vm.CopyEntryWithStackCommand.ExecuteAsync(null);

        Assert.False(touched, "a null row must decline rather than copy an empty string");
    }
}
