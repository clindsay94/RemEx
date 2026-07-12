using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

public class FileTransferQueueTests
{
    // Synchronous UI-thread marshaller so the queue is deterministic in tests.
    private static FileTransferQueue NewQueue() => new(action => action());

    [Fact]
    public async Task Enqueue_RunsWorkAndReachesDone()
    {
        var queue = NewQueue();
        var item = queue.Enqueue(FileTransferQueueKind.Upload, "a.txt", (_, _) => Task.CompletedTask);

        await item.Completion.Task;

        item.State.Should().Be(TransferState.Done);
        item.Progress.Should().Be(100.0);
        queue.Items.Should().ContainSingle().Which.Should().BeSameAs(item);
    }

    [Fact]
    public async Task Enqueue_ProcessesFifoOneActiveAtATime()
    {
        var queue = NewQueue();
        var gate = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource();

        var first = queue.Enqueue(FileTransferQueueKind.Upload, "first", async (_, _) =>
        {
            firstStarted.TrySetResult();
            await gate.Task;
        });
        var second = queue.Enqueue(FileTransferQueueKind.Download, "second", (_, _) => Task.CompletedTask);

        await firstStarted.Task;

        // While the first is running the second must still be queued (one active per queue).
        first.State.Should().Be(TransferState.Active);
        second.State.Should().Be(TransferState.Queued);

        gate.SetResult();
        await second.Completion.Task;

        first.State.Should().Be(TransferState.Done);
        second.State.Should().Be(TransferState.Done);
        queue.Items.Select(i => i.FileName).Should().ContainInOrder("first", "second");
    }

    [Fact]
    public async Task Work_ThatThrows_MarksItemFailedWithMessage()
    {
        var queue = NewQueue();
        var item = queue.Enqueue(FileTransferQueueKind.Upload, "boom", (_, _) => throw new InvalidOperationException("nope"));

        await item.Completion.Task;

        item.State.Should().Be(TransferState.Failed);
        item.ErrorMessage.Should().Be("nope");
    }

    [Fact]
    public async Task Cancel_WhileQueued_NeverRunsWorkAndMarksCancelled()
    {
        var queue = NewQueue();
        var gate = new TaskCompletionSource();
        var firstStarted = new TaskCompletionSource();

        var first = queue.Enqueue(FileTransferQueueKind.Upload, "first", async (_, _) =>
        {
            firstStarted.TrySetResult();
            await gate.Task;
        });

        var ran = false;
        var second = queue.Enqueue(FileTransferQueueKind.Upload, "second", (_, _) => { ran = true; return Task.CompletedTask; });

        await firstStarted.Task;
        second.CancelCommand.Execute(null);
        gate.SetResult();

        await second.Completion.Task;

        second.State.Should().Be(TransferState.Cancelled);
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_WhileActive_CancelsThroughToken()
    {
        var queue = NewQueue();
        var started = new TaskCompletionSource();

        var item = queue.Enqueue(FileTransferQueueKind.Download, "big", async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });

        await started.Task;
        item.CancelCommand.Execute(null);

        await item.Completion.Task;
        item.State.Should().Be(TransferState.Cancelled);
    }

    [Fact]
    public async Task ClearCompleted_RemovesOnlyTerminalItems()
    {
        var queue = NewQueue();
        var gate = new TaskCompletionSource();
        var activeStarted = new TaskCompletionSource();

        var done = queue.Enqueue(FileTransferQueueKind.Upload, "done", (_, _) => Task.CompletedTask);
        await done.Completion.Task;

        var active = queue.Enqueue(FileTransferQueueKind.Upload, "active", async (_, _) =>
        {
            activeStarted.TrySetResult();
            await gate.Task;
        });
        await activeStarted.Task;

        queue.ClearCompleted();

        queue.Items.Should().ContainSingle().Which.Should().BeSameAs(active);

        gate.SetResult();
        await active.Completion.Task;
    }
}
