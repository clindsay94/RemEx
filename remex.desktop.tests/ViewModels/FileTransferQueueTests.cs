using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using System.IO;
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
    /// <summary>
    /// An unrecognised failure is reported in the user's language, not as the exception's text.
    /// </summary>
    /// <remarks>
    /// This test previously asserted the opposite - that the raw message "nope" reached
    /// <c>ErrorMessage</c> - which is precisely the defect RemEx-s4p4 exists to remove: the queue
    /// panel renders this string, so every exception thrown anywhere under a transfer was
    /// user-facing developer English in all nine languages.
    /// </remarks>
    public async Task Work_ThatThrows_ReportsALocalizedMessageRatherThanTheExceptionText()
    {
        var queue = NewQueue();
        var item = queue.Enqueue(FileTransferQueueKind.Upload, "boom", (_, _) => throw new InvalidOperationException("nope"));

        await item.Completion.Task;

        item.State.Should().Be(TransferState.Failed);
        item.ErrorMessage.Should().NotBe("nope", "the exception's own text must never reach the queue panel");
        item.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        item.ErrorMessage.Should().NotBe("FileTransfer_ErrGeneric",
            "a key resolving to its own name means the .resx entry is missing");
    }

    [Fact]
    /// <summary>
    /// A disk failure names the disk, instead of telling the user to check their pairing.
    /// </summary>
    /// <remarks>
    /// RemEx-owc3 added an explicit flush so a full or unplugged destination fails loudly rather than
    /// truncating silently. It then fell through to the generic sentence — "check the connected device
    /// is still paired" — which is advice about the phone for a problem with the USB stick. The person
    /// most likely to hit this is the one least able to work out what actually happened.
    /// </remarks>
    public async Task Work_ThatFailsOnDisk_NamesTheDiskRatherThanThePairing()
    {
        var queue = NewQueue();
        var item = queue.Enqueue(
            FileTransferQueueKind.Download, "big.iso", (_, _) => throw new IOException("There is not enough space on the disk."));

        await item.Completion.Task;

        item.State.Should().Be(TransferState.Failed);
        item.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        item.ErrorMessage.Should().NotBe("FileTransfer_ErrDestinationUnavailable",
            "a key resolving to its own name means the .resx entry is missing");
        item.ErrorMessage.Should().NotBe(LocalizationService.Instance["FileTransfer_ErrGeneric"],
            "the generic message tells the user to check their pairing, which is the wrong advice here");
        item.ErrorMessage.Should().Be(LocalizationService.Instance["FileTransfer_ErrDestinationUnavailable"]);
        item.ErrorMessage.Should().NotContain("enough space on the disk",
            "the exception's own wording must not reach the panel — it is English in all nine languages");
    }

    [Fact]
    /// <summary>
    /// An upload that fails on disk describes the SOURCE, not a destination folder.
    /// </summary>
    /// <remarks>
    /// THE CASE THAT MADE THIS DIRECTION-AWARE. Uploads read a local file, and the commonest
    /// IOException there is a sharing violation — the document is open in Word or Excel. Reporting
    /// that as "the download could not be saved, the drive you chose may be out of space" is not
    /// vague, it is confidently wrong, which is a worse failure than the generic sentence this bead
    /// set out to replace. (RemEx-6tvh)
    /// </remarks>
    public async Task Upload_ThatFailsOnDisk_DescribesTheSourceFileNotTheDestination()
    {
        var queue = NewQueue();
        var item = queue.Enqueue(
            FileTransferQueueKind.Upload, "budget.xlsx",
            (_, _) => throw new IOException("The process cannot access the file because it is being used by another process."));

        await item.Completion.Task;

        item.ErrorMessage.Should().Be(LocalizationService.Instance["FileTransfer_ErrSourceUnavailable"]);
        item.ErrorMessage.Should().NotBe(LocalizationService.Instance["FileTransfer_ErrDestinationUnavailable"],
            "an upload has no destination folder on this machine to be out of space");
        item.ErrorMessage.Should().NotBe("FileTransfer_ErrSourceUnavailable",
            "a key resolving to its own name means the .resx entry is missing");
    }

    [Fact]
    /// <summary>
    /// The more specific failures keep their own messages even though they can derive from IOException.
    /// </summary>
    /// <remarks>
    /// <c>FileTransferBacklogException</c> DERIVES FROM <c>IOException</c>, so the new arm could have
    /// swallowed it and replaced a precise explanation with a generic disk one. It does not, and the
    /// ordering that prevents it is enforced by the compiler rather than by this test — hoisting the
    /// arm is CS8510, an unreachable pattern. (An injection was run specifically to check that, and it
    /// failed to build rather than failing this test.) What this DOES pin is the mapping itself: that a
    /// destination which cannot keep up still gets its own sentence rather than the disk one, which no
    /// compiler can check. (RemEx-6tvh)
    /// </remarks>
    public async Task ADestinationTooSlowFailureIsNotReportedAsADiskProblem()
    {
        var queue = NewQueue();
        var item = queue.Enqueue(
            FileTransferQueueKind.Download, "big.iso", (_, _) => throw new FileTransferBacklogException(queuedBytes: 300_000_000, limitBytes: 268_435_456));

        await item.Completion.Task;

        item.ErrorMessage.Should().Be(LocalizationService.Instance["FileTransfer_ErrDestinationTooSlow"]);
        item.ErrorMessage.Should().NotBe(LocalizationService.Instance["FileTransfer_ErrDestinationUnavailable"]);
    }

    /// <summary>
    /// A host refusal IS shown verbatim - it is the one message written for a user.
    /// </summary>
    /// <remarks>
    /// The counterpart to the test above, and the reason dispatch is by TYPE: replacing this with a
    /// generic sentence would discard the most useful text in the whole flow, which is the mistake
    /// RemEx-mznc caught on the PC's other error surface.
    /// </remarks>
    [Fact]
    public async Task Work_ThatFailsOnTheHost_ShowsTheHostsOwnWording()
    {
        const string HostReply = "Adding a shared folder must be done on the phone.";
        var queue = NewQueue();
        var item = queue.Enqueue(
            FileTransferQueueKind.Upload,
            "boom",
            (_, _) => throw Remex.Desktop.Services.FileTransfer.FileTransferHostException.ForHostError(
                HostReply, "developer context"));

        await item.Completion.Task;

        item.State.Should().Be(TransferState.Failed);
        item.ErrorMessage.Should().Be(HostReply);
    }

    /// <summary>An integrity failure gets its own sentence, distinct from the generic one.</summary>
    [Fact]
    public async Task Work_ThatFailsIntegrity_ReportsTheIntegrityMessage()
    {
        var queue = NewQueue();
        var item = queue.Enqueue(
            FileTransferQueueKind.Download,
            "boom",
            (_, _) => throw new Remex.Desktop.Services.FileTransfer.FileTransferIntegrityException());

        await item.Completion.Task;

        item.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        item.ErrorMessage.Should().NotContain("SHA-256", "the developer wording must not reach the user");
        item.ErrorMessage.Should().NotBe("FileTransfer_ErrIntegrity");
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

    /// <summary>
    /// The labels read the localizer at get-time, so switching language changes what they return
    /// without anything on the item changing. Without the queue's fan-out the row keeps rendering the
    /// previous language until its state happens to move.
    /// </summary>
    [Fact]
    public async Task LanguageSwitch_RefreshesQueuedItemLabels()
    {
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("en");
            using var queue = NewQueue();
            var item = queue.Enqueue(FileTransferQueueKind.Upload, "a.txt", (_, _) => Task.CompletedTask);
            await item.Completion.Task;
            var english = item.ModeLabel;

            var raised = new List<string?>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            LocalizationService.Instance.SetCulture("fr");

            raised.Should().Contain(nameof(FileTransferQueueItem.ModeLabel));
            raised.Should().Contain(nameof(FileTransferQueueItem.StateLabel));
            item.ModeLabel.Should().NotBe(english, "the French resource differs from the English one");
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }

    /// <summary>
    /// The localizer is a process-lifetime singleton, so a queue that stayed subscribed would keep
    /// itself and every item it holds alive forever.
    /// </summary>
    [Fact]
    public async Task Dispose_DetachesFromTheLocalizer()
    {
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture("en");
            var queue = NewQueue();
            var item = queue.Enqueue(FileTransferQueueKind.Upload, "a.txt", (_, _) => Task.CompletedTask);
            await item.Completion.Task;
            queue.Dispose();

            var raised = new List<string?>();
            item.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            LocalizationService.Instance.SetCulture("fr");

            raised.Should().BeEmpty();
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }
}
