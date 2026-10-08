using System;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// A finished transfer says "Verified" and carries its hash only when the hash was really compared
/// (2026-10-08 redesign): before this, every success said "Done" and the hash the client had just checked
/// was thrown away.
/// </summary>
public class FileTransferQueueVerifiedTests
{
    private const string AbcBase64 = "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=";
    private const string AbcHex = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static Action<Action> UiThreadStandIn()
    {
        var uiThread = new object();
        return action =>
        {
            lock (uiThread)
                action();
        };
    }

    [Fact]
    public async Task ATransferThatReportsItsHash_FinishesVerified_WithTheHashInHex()
    {
        using var queue = new FileTransferQueue(UiThreadStandIn());
        var sawVerifying = false;

        var item = queue.Enqueue(FileTransferQueueKind.Download, "a.bin", (progress, _) =>
        {
            progress.ReportVerifying();
            sawVerifying = queue.Items[0].State == TransferState.Verifying;
            progress.ReportVerified(AbcBase64);
            return Task.CompletedTask;
        });
        await item.Completion.Task;

        sawVerifying.Should().BeTrue("the row says Verifying while the hashes are compared");
        item.State.Should().Be(TransferState.Done);
        item.IsVerified.Should().BeTrue();
        item.VerifiedSha256Hex.Should().Be(AbcHex);
        item.StateLabel.Should().NotBe(LocalizationService.Instance["FileTransfer_QueueStateDone"]);
    }

    [Fact]
    public async Task ATransferThatComparedNoHash_StaysDone_NotVerified()
    {
        using var queue = new FileTransferQueue(UiThreadStandIn());

        var item = queue.Enqueue(FileTransferQueueKind.Upload, "b.bin", (_, _) => Task.CompletedTask);
        await item.Completion.Task;

        item.State.Should().Be(TransferState.Done);
        item.IsVerified.Should().BeFalse();
        item.VerifiedSha256Hex.Should().BeNull();
        item.StateLabel.Should().Be(LocalizationService.Instance["FileTransfer_QueueStateDone"]);
    }

    [Fact]
    public async Task AMalformedHash_NeverEarnsVerified()
    {
        using var queue = new FileTransferQueue(UiThreadStandIn());

        var item = queue.Enqueue(FileTransferQueueKind.Download, "c.bin", (progress, _) =>
        {
            progress.ReportVerified("not a hash");
            return Task.CompletedTask;
        });
        await item.Completion.Task;

        item.IsVerified.Should().BeFalse();
    }

    [Fact]
    public async Task ATransferThatFailsAfterVerifying_IsFailed_NotVerified()
    {
        using var queue = new FileTransferQueue(UiThreadStandIn());

        var item = queue.Enqueue(FileTransferQueueKind.Download, "d.bin", (progress, _) =>
        {
            progress.ReportVerifying();
            throw new FileTransferIntegrityException();
        });
        await item.Completion.Task;

        item.State.Should().Be(TransferState.Failed);
        item.IsVerified.Should().BeFalse();
    }
}
