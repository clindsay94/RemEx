using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Source guards for RemEx-x6a70.1. The dialog now owns the retry loop, so
/// <c>CompletePairingAsync</c> must be called from exactly one place — inside
/// <c>PairWithDialogAsync</c> — and the old <c>PromptForPinAsync</c> path must be gone entirely. A
/// second call site (e.g. a leftover direct call alongside the dialog) would silently attempt
/// pairing twice per connection, which is exactly the kind of regression a source scan catches and
/// a behavioural test would not, because nothing observable distinguishes "called once" from
/// "called twice but the second result is discarded".
/// </summary>
public class PairingFlowGuardTests
{
    [Fact]
    public void CompletePairingAsync_IsCalledExactlyOnce_InsidePairWithDialogAsync()
    {
        var source = ConnectionViewModelSource();

        var matches = Regex.Matches(source, @"CompletePairingAsync\(");
        matches.Count.Should().Be(1,
            "CompletePairingAsync must be invoked from exactly one place — the delegate handed to " +
            "the pairing dialog — not called again directly alongside it");

        var callIndex = matches[0].Index;
        var methodStart = source.LastIndexOf("private async Task<bool> PairWithDialogAsync(", callIndex, StringComparison.Ordinal);
        methodStart.Should().BeGreaterThanOrEqualTo(0,
            "the one CompletePairingAsync call must live inside PairWithDialogAsync");
    }

    [Fact]
    public void PromptForPinAsync_NoLongerExists()
    {
        var source = ConnectionViewModelSource();

        source.Should().NotContain("PromptForPinAsync",
            "the old string?-returning prompt was replaced by PairWithDialogAsync, which owns the " +
            "whole verify/retry loop instead of handing a bare PIN back to the caller");
    }

    [Fact]
    public void PairingDialogAxaml_SuccessRowIsBoundToIsSucceeded_AndTheTextBoxToCanEdit()
    {
        var markup = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "PairingDialog.axaml"));

        markup.Should().Contain("IsVisible=\"{Binding IsSucceeded}\"",
            "the success row must appear only once the dialog has actually verified the PIN");
        markup.Should().Contain("IsEnabled=\"{Binding CanEdit}\"",
            "the PIN field must stop accepting input while busy and after success, not just while busy");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    private static string ConnectionViewModelSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "ConnectionViewModel.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
