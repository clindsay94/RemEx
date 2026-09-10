using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// The join: a colliding paste reaches the user as a typed prompt, and their answer reaches the
/// host on the retry (RemEx-6vd8, PC half).
/// </summary>
/// <remarks>
/// <para>
/// **THIS FILE IS THE POINT OF THE CONSTRUCTOR SEAM, and AGENTS.md's splitting rule is why it
/// exists at all.** A pure <c>FileConflictPolicy</c> with a full test count and no reachable caller
/// is the exact shape this repo has been bitten by four times: the logic half lands looking like
/// progress, the wiring half sits on the board, and the defect is always AT THE JOIN. So the wiring
/// is asserted here, through the real <c>PasteAsync</c>, against a host that actually refuses.
/// </para>
/// <para>
/// The view model built its own <c>FileTransferClient</c> from a <c>ConnectionViewModel</c> that no
/// test can make speak, which is why none of this was reachable before. The optional client
/// parameter changes nothing in production — <c>ShellViewModel</c> passes none.
/// </para>
/// </remarks>
public sealed class FileTransferConflictPromptTests
{
    private const string HostProse = "A file with that name already exists.";

    /// <summary>
    /// A host that refuses the first paste of each item with a collision, then accepts the retry.
    /// </summary>
    /// <remarks>
    /// Also answers the browse that <c>PasteAsync</c> issues when it finishes: without that the
    /// client waits out its 30-second control-request timeout and the test looks like a hang rather
    /// than a missing reply.
    /// </remarks>
    private sealed class CollidingHost : IFileTransferConnection
    {
        private readonly string _errorCode;

        public CollidingHost(string errorCode = FileTransferErrorCodes.DestinationExists)
            => _errorCode = errorCode;

        public List<FileManageRequest> Manages { get; } = [];

        /// <summary>The name the host invents when it is asked to keep both.</summary>
        public string ResolvedName { get; init; } = "report (2).pdf";

        /// <summary>
        /// Refuses every retry too — the pathological case the round cap exists for, and the shape
        /// <c>resolved_name_taken</c> really can take when something else is claiming names as fast
        /// as the host picks them.
        /// </summary>
        public bool AlwaysCollides { get; init; }

        public event Action<RemexMessage>? FileTransferMessageReceived;

        public Task SendAsync(RemexMessage message)
        {
            if (message.FileManageRequest is { } manage)
            {
                Manages.Add(manage);

                // No answer given means the collision stands; any answer at all is accepted, which
                // is what lets a test tell "the retry carried the resolution" from "it retried".
                var accepted = !AlwaysCollides && !string.IsNullOrEmpty(manage.ConflictResolution);

                FileTransferMessageReceived?.Invoke(new RemexMessage
                {
                    Type = MessageTypes.FileManageResponse,
                    FileManageResponse = new FileManageResponse
                    {
                        RequestId = manage.RequestId,
                        Success = accepted,
                        ErrorMessage = accepted ? null : HostProse,
                        ErrorCode = accepted ? null : _errorCode,
                        ConflictingName = accepted ? null : "report.pdf",
                        ResolvedName = accepted && manage.ConflictResolution == FileConflictResolutions.KeepBoth
                            ? ResolvedName
                            : null,
                    },
                });
            }
            else if (message.FileBrowseRequest is { } browse)
            {
                FileTransferMessageReceived?.Invoke(new RemexMessage
                {
                    Type = MessageTypes.FileBrowseResponse,
                    FileBrowseResponse = new FileBrowseResponse { RequestId = browse.RequestId, Entries = [] },
                });
            }

            return Task.CompletedTask;
        }
    }

    private static readonly FileSharedRoot WritableRoot = new()
    {
        RootId = "root-1",
        DisplayName = "Shared",
        IsWritable = true,
        CanRename = true,
        CanMove = true,
        CanDelete = true,
    };

    /// <summary>A view model with one file on the clipboard and a different folder open.</summary>
    /// <remarks>
    /// THE ORDER OF THE LAST TWO STEPS IS LOAD-BEARING. The clipboard remembers the folder it was
    /// filled FROM, so filling it after the navigation would make source and destination the same
    /// path — and <c>PasteAsync</c> skips those as a no-op, leaving a test that passes while
    /// exercising nothing.
    /// </remarks>
    private static async Task<FileTransferViewModel> ArmedWithAClipboardAsync(FileTransferClient client, bool asCut = false)
    {
        var vm = new FileTransferViewModel(new ConnectionViewModel(), client: client)
        {
            // Normally set by RefreshCapabilityFlags off the host's advertised ops; the fake never
            // sends a roots response, so the gate is set directly rather than faked into existence.
            SupportsCopyMove = true,
            SelectedRemoteRoot = WritableRoot,
        };

        // SELECTING A ROOT KICKS OFF A BROWSE OF ITS OWN, fire-and-forget. Its continuation blanks
        // StatusText and rebuilds the listing, so a test that raced it would be asserting against a
        // view model still mid-refresh — and intermittently, which is the worst way to find out.
        await WaitForAsync(() => !vm.IsLoading, "the initial browse of the selected root should settle");

        vm.SetSelectedEntries([new FileEntry { Name = "report.pdf", IsDirectory = false }]);

        if (asCut)
            vm.CutSelectionCommand.Execute(null);
        else
            vm.CopySelectionCommand.Execute(null);

        // Somewhere else, or the paste is a no-op into the folder the file came from.
        vm.RemotePath = "/reports";
        return vm;
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
            await Task.Delay(10);

        condition().Should().BeTrue(because);
    }

    [Fact]
    public async Task ACollidingPasteAsksTheUserInsteadOfPrintingTheHostsProse()
    {
        var host = new CollidingHost();
        using var client = new FileTransferClient(host);
        using var vm = await ArmedWithAClipboardAsync(client);

        var paste = vm.PasteCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.PendingConflict is not null, "the collision must reach the user as a prompt");

        // THE STATE IS TYPED. Before this, the host's English landed in StatusText and the user's
        // only remaining move was to rename something by hand.
        vm.PendingConflict!.ErrorCode.Should().Be("destination_exists");
        vm.PendingConflict.ConflictingName.Should().Be("report.pdf");
        vm.PendingConflict.CanReplace.Should().BeTrue();
        vm.PendingConflict.CanKeepBoth.Should().BeTrue();
        vm.HasConflictPrompt.Should().BeTrue();

        // AND THE PROSE IS NOWHERE ON SCREEN. Not merely "a code is also present": the host's
        // sentence is untranslatable and rewording it would silently change what the PC says.
        vm.StatusText.Should().NotContain(HostProse);

        vm.KeepBothOnConflictCommand.Execute(null);
        await paste;

        vm.PendingConflict.Should().BeNull("the prompt comes down once it has been answered");
    }

    [Fact]
    public async Task KeepBothRetriesWithTheHostsOwnWireValueAndReportsTheNameItChose()
    {
        var host = new CollidingHost();
        using var client = new FileTransferClient(host);
        using var vm = await ArmedWithAClipboardAsync(client);

        var paste = vm.PasteCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.PendingConflict is not null, "the collision must reach the user");
        vm.KeepBothOnConflictCommand.Execute(null);
        await paste;

        host.Manages.Should().HaveCount(2, "the answer is carried by a retry, not by a separate message");
        host.Manages[0].ConflictResolution.Should().BeNull("nobody had been asked yet");
        host.Manages[1].ConflictResolution.Should().Be("keep_both");

        // The host chose the name, so the user is told what it is — otherwise they believe they have
        // report.pdf while the file on disk is report (2).pdf.
        vm.StatusText.Should().Contain("report (2).pdf");
    }

    [Fact]
    public async Task ReplaceRetriesWithTheOtherWireValue()
    {
        var host = new CollidingHost();
        using var client = new FileTransferClient(host);
        using var vm = await ArmedWithAClipboardAsync(client);

        var paste = vm.PasteCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.PendingConflict is not null, "the collision must reach the user");
        vm.ReplaceOnConflictCommand.Execute(null);
        await paste;

        host.Manages.Should().HaveCount(2);
        host.Manages[1].ConflictResolution.Should().Be("replace");
    }

    [Fact]
    public async Task ReplaceIsRefusedForACodeThePolicyWithholdsItFrom()
    {
        // THE GUARD THAT MATTERS MOST, and the reason the offer list is re-checked where the answer
        // arrives rather than only where the buttons are drawn. Replacing a FOLDER with a file means
        // deleting a whole directory tree, and nothing undoes it — so a view that bound the wrong
        // visibility, or a keyboard route reaching a collapsed button, must not be able to ask for it.
        var host = new CollidingHost(FileTransferErrorCodes.DestinationIsDifferentKind);
        using var client = new FileTransferClient(host);
        using var vm = await ArmedWithAClipboardAsync(client);

        var paste = vm.PasteCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.PendingConflict is not null, "the collision must reach the user");

        vm.PendingConflict!.CanReplace.Should().BeFalse();
        vm.ReplaceOnConflictCommand.Execute(null);

        // Still waiting: the withheld answer was dropped rather than acted on.
        paste.IsCompleted.Should().BeFalse();
        vm.PendingConflict.Should().NotBeNull();

        vm.SkipOnConflictCommand.Execute(null);
        await paste;

        host.Manages.Should().HaveCount(1, "skip declines to retry, so nothing more goes on the wire");
    }

    [Fact]
    public async Task AHostThatKeepsRefusingEndsTheItemInsteadOfAskingForever()
    {
        // THE CAP IS COUNTED IN REQUESTS, AND THE LAST ROUND MUST NOT PUT UP A PROMPT IT CANNOT
        // HONOUR. Counting prompts instead would collect a third answer, close the prompt and send
        // nothing — the user watches themselves choose and the choice does nothing. So: three
        // requests, two questions, then the item is reported as left behind.
        var host = new CollidingHost(FileTransferErrorCodes.ResolvedNameTaken) { AlwaysCollides = true };
        using var client = new FileTransferClient(host);
        using var vm = await ArmedWithAClipboardAsync(client);

        var paste = vm.PasteCommand.ExecuteAsync(null);

        var prompts = 0;
        while (true)
        {
            await WaitForAsync(
                () => vm.PendingConflict is not null || paste.IsCompleted,
                "the paste must either ask again or finish, never stall");

            if (vm.PendingConflict is not { } current) break;

            prompts++;
            prompts.Should().BeLessThan(10, "an unbounded retry loop is the defect the cap exists for");
            vm.KeepBothOnConflictCommand.Execute(null);

            // WAITING FOR "not this prompt any more" RATHER THAN "no prompt", because the next round
            // can put a fresh one up before the test ever observes the gap. Each round builds a new
            // FileConflictPrompt, so reference identity is the reliable signal.
            await WaitForAsync(
                () => !ReferenceEquals(vm.PendingConflict, current),
                "the answered prompt must be replaced or withdrawn");
        }

        await paste;

        prompts.Should().Be(2, "three requests leave room for two answers");
        host.Manages.Should().HaveCount(3);
        vm.StatusText.Should().Contain("report.pdf", "the user has to be told which item was left behind");
        vm.PendingConflict.Should().BeNull();
    }

    [Fact]
    public async Task StoppingThePasteLeavesACutSelectionOnTheClipboard()
    {
        // Clearing the clipboard after the user stopped half way would leave the un-moved items with
        // nowhere left to be pasted from — a worse outcome than the collision they were avoiding.
        var host = new CollidingHost();
        using var client = new FileTransferClient(host);
        using var vm = await ArmedWithAClipboardAsync(client, asCut: true);

        var paste = vm.PasteCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.PendingConflict is not null, "the collision must reach the user");
        vm.CancelOnConflictCommand.Execute(null);
        await paste;

        vm.HasClipboard.Should().BeTrue();
        host.Manages.Should().HaveCount(1, "cancel stops the batch where it stands");
        vm.IsLoading.Should().BeFalse("the page must not be left spinning after the user stopped");
    }

    [Fact]
    public async Task DisposingWhileAPromptIsOpenDoesNotStrandThePaste()
    {
        // Without this the paste stays parked on a TaskCompletionSource nothing will ever complete,
        // holding IsLoading on for the life of the process.
        var host = new CollidingHost();
        using var client = new FileTransferClient(host);
        var vm = await ArmedWithAClipboardAsync(client);

        var paste = vm.PasteCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.PendingConflict is not null, "the collision must reach the user");

        vm.Dispose();

        // THE PROMPT COMING DOWN IS THE ASSERTION, not the whole task finishing. Dispose unsubscribes
        // the client from the connection, so the refresh that follows a paste gets no reply and waits
        // out its own 30-second bound — awaiting the task would measure that timeout rather than this
        // fix, and would do it slowly.
        await WaitForAsync(
            () => vm.PendingConflict is null,
            "disposal must release the paste rather than leave it parked on an answer nobody can give");

        // Nothing more may be asked of the host on this item: the released answer is Cancel.
        host.Manages.Should().HaveCount(1);
        _ = paste;
    }
}
