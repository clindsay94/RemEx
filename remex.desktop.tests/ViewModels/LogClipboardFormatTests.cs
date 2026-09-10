using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Copying selected log rows produces something worth pasting (RemEx-7xhln).
/// </summary>
/// <remarks>
/// The list has offered <c>SelectionMode="Multiple"</c> since it shipped with nothing bound to it —
/// no Copy, no Ctrl+C, and no <c>SelectedItems</c> binding at all, so the view model could not even
/// observe what the user selected. Selecting rows was a gesture the app accepted and discarded.
/// </remarks>
public class LogClipboardFormatTests
{
    private static LogEntry Entry(int second, string message) =>
        new(new DateTime(2026, 8, 9, 12, 0, second, DateTimeKind.Utc),
            LogLevel.Information, "Cat", message, null);

    [Fact]
    public void TheCopyIsInDISPLAYOrderRatherThanTheOrderTheUserClicked()
    {
        // THE ONE REAL DECISION IN THIS CHANGE. Avalonia reports SelectedItems in selection order, so
        // ctrl-clicking three lines bottom-up would paste an incident backwards — and a log read out
        // of sequence is worse than no log, because the reader infers a causal order that never
        // happened. The selection here is deliberately supplied reversed.
        var first = Entry(1, "first");
        var second = Entry(2, "second");
        var third = Entry(3, "third");

        var text = DiagnosticLogsViewModel.FormatForClipboard(
            displayed: [first, second, third],
            selected: [third, first]);

        var lines = text.Split(Environment.NewLine);

        Assert.Equal(2, lines.Length);
        Assert.Contains("first", lines[0], StringComparison.Ordinal);
        Assert.Contains("third", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySelectionCopiesNothingRatherThanABlankLine()
    {
        // So the command can decline to touch the clipboard at all. Returning "" and pasting it are
        // different outcomes: the second silently replaces whatever the user already had.
        Assert.Equal(
            string.Empty,
            DiagnosticLogsViewModel.FormatForClipboard([Entry(1, "a")], []));
    }

    [Fact]
    public void OnlyTheSelectedRowsAreCopied()
    {
        var wanted = Entry(2, "wanted");

        var text = DiagnosticLogsViewModel.FormatForClipboard(
            displayed: [Entry(1, "skipped"), wanted, Entry(3, "also-skipped")],
            selected: [wanted]);

        Assert.Contains("wanted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("skipped", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLineIsWhatTheLISTSHOWS_ExceptionIncluded()
    {
        // NOT A SECOND FORMAT. LogEntry.ToString() is what the live list renders, so the clipboard
        // carries what the user was looking at - and the exception block is the part anyone pasting a
        // log into a bug report actually needs. A hand-rolled format here would drift from the screen
        // and drop it.
        var faulted = new LogEntry(
            new DateTime(2026, 8, 9, 12, 0, 4, DateTimeKind.Utc),
            LogLevel.Error, "Transfer", "push failed", new InvalidOperationException("boom"));

        var text = DiagnosticLogsViewModel.FormatForClipboard([faulted], [faulted]);

        Assert.Equal(faulted.ToString(), text);
        Assert.Contains("boom", text, StringComparison.Ordinal);
    }
}
