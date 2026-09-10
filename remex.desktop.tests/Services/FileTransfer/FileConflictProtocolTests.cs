using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services.FileTransfer;

/// <summary>
/// The PC half of the filename-collision protocol, at the wire (RemEx-6vd8).
/// </summary>
/// <remarks>
/// <para>
/// <c>ManageAsync</c> read <c>Success</c> and discarded the rest of the reply, so <c>errorCode</c>,
/// <c>conflictingName</c> and <c>resolvedName</c> were deserialized and dropped — and it took no
/// <c>conflictResolution</c> at all, so there was no way to answer a collision even if the PC had
/// noticed one. Everything here would have passed vacuously before that plumbing existed only
/// because the calls did not compile.
/// </para>
/// <para>
/// THE WIRE VALUES ARE ASSERTED AS LITERALS, never against the constants that produce them.
/// <c>ResolutionFor(Replace).Should().Be(FileConflictResolutions.Replace)</c> is an assertion that
/// cannot fail: rename the constant's VALUE and both sides move together, while the phone and the
/// host — which have their own copies — do not. "replace" and "keep_both" spelled out are the only
/// form of this test that can catch a protocol break.
/// </para>
/// </remarks>
public sealed class FileConflictProtocolTests
{
    /// <summary>
    /// Answers every manage request with whatever the test scripted, and keeps what was sent.
    /// </summary>
    /// <remarks>
    /// Replies from INSIDE the send, as <see cref="DownloadUnwindTests"/> does, because the client
    /// registers its waiter before sending — so a synchronous answer is delivered to a waiter that
    /// already exists and no test needs a timing assumption.
    /// </remarks>
    private sealed class ScriptedHost : IFileTransferConnection
    {
        private readonly Func<FileManageRequest, int, FileManageResponse> _answer;

        public ScriptedHost(Func<FileManageRequest, int, FileManageResponse> answer) => _answer = answer;

        public List<FileManageRequest> Sent { get; } = [];

        public event Action<RemexMessage>? FileTransferMessageReceived;

        public Task SendAsync(RemexMessage message)
        {
            if (message.FileManageRequest is { } request)
            {
                Sent.Add(request);
                FileTransferMessageReceived?.Invoke(new RemexMessage
                {
                    Type = MessageTypes.FileManageResponse,
                    FileManageResponse = _answer(request, Sent.Count - 1),
                });
            }

            return Task.CompletedTask;
        }
    }

    private static FileManageResponse Accepted(FileManageRequest request, string? resolvedName = null) =>
        new() { RequestId = request.RequestId, Success = true, ResolvedName = resolvedName };

    private static FileManageResponse Refused(
        FileManageRequest request, string? message, string? code = null, string? conflictingName = null) =>
        new()
        {
            RequestId = request.RequestId,
            Success = false,
            ErrorMessage = message,
            ErrorCode = code,
            ConflictingName = conflictingName,
        };

    // ─── The answer reaches the host ──────────────────────────────────────────

    [Theory]
    [InlineData(FileConflictAction.Replace, "replace")]
    [InlineData(FileConflictAction.KeepBoth, "keep_both")]
    public async Task TheChosenResolutionTravelsToTheHostVerbatim(FileConflictAction action, string wireValue)
    {
        var host = new ScriptedHost((request, _) => Accepted(request));
        using var client = new FileTransferClient(host);

        await client.CopyRemoteAsync(
            "root-1", "in/report.pdf", "out/report.pdf", overwrite: false,
            FileConflictPolicy.ResolutionFor(action), CancellationToken.None);

        host.Sent.Should().ContainSingle().Which.ConflictResolution.Should().Be(wireValue);
    }

    [Theory]
    [InlineData(FileConflictAction.Skip)]
    [InlineData(FileConflictAction.Cancel)]
    public void DecliningToRetrySendsNoResolutionAtAll(FileConflictAction action)
    {
        // NOT A RESOLUTION THE HOST UNDERSTANDS — it is the PC declining to retry. A "skip" spelled
        // onto the wire would be an unrecognised value, which the host's ConflictResolver maps back
        // to the plain refusal: the same outcome by a longer route, and one more token to keep in
        // step across three codebases.
        FileConflictPolicy.ResolutionFor(action).Should().BeNull();
    }

    [Fact]
    public async Task AFirstAttemptCarriesNoResolution()
    {
        // NULL IS "NOBODY HAS BEEN ASKED", and the host is documented to refuse rather than guess
        // when it sees that. A default of "replace" or "keep_both" here would silently overwrite or
        // rename files for a user who was never shown a prompt.
        var host = new ScriptedHost((request, _) => Accepted(request));
        using var client = new FileTransferClient(host);

        await client.MoveRemoteAsync(
            "root-1", "in/report.pdf", "out/report.pdf", overwrite: false, null, CancellationToken.None);

        host.Sent.Should().ContainSingle().Which.ConflictResolution.Should().BeNull();
    }

    // ─── The host's answer reaches the caller ─────────────────────────────────

    [Fact]
    public async Task ACollisionArrivesAsStructuredStateRatherThanAThrow()
    {
        const string prose = "A file with that name already exists.";
        var host = new ScriptedHost((request, _) =>
            Refused(request, prose, FileTransferErrorCodes.DestinationExists, "report.pdf"));
        using var client = new FileTransferClient(host);

        var outcome = await client.CopyRemoteAsync(
            "root-1", "in/report.pdf", "out/report.pdf", overwrite: false, null, CancellationToken.None);

        outcome.IsConflict.Should().BeTrue("a collision is a question the user can answer, not a failure");
        outcome.ErrorCode.Should().Be("destination_exists");
        outcome.ConflictingName.Should().Be(
            "report.pdf", "the prompt has to be able to name the file it is asking about");
        outcome.Success.Should().BeFalse();
    }

    [Fact]
    public async Task AKeepBothThatSucceededReportsTheNameTheHostChose()
    {
        // THE HOST CHOOSES THE NAME AND THEN SAYS SO. Dropping resolvedName leaves the user believing
        // they have report.pdf while the file on disk is report (2).pdf — and the PC cannot work the
        // name out for itself, because only the host can see the rest of that directory.
        var host = new ScriptedHost((request, _) => Accepted(request, resolvedName: "report (2).pdf"));
        using var client = new FileTransferClient(host);

        var outcome = await client.CopyRemoteAsync(
            "root-1", "in/report.pdf", "out/report.pdf", overwrite: false, "keep_both", CancellationToken.None);

        outcome.Success.Should().BeTrue();
        outcome.IsConflict.Should().BeFalse();
        outcome.ResolvedName.Should().Be("report (2).pdf");
    }

    [Fact]
    public async Task ARefusalWithNoCodeIsStillAThrownHostException()
    {
        // THE SPLIT THAT KEEPS THE EXISTING CATCH SITES WORKING. Read-only root, disk full, source
        // vanished — none of those has an answer a prompt could collect, so they must keep arriving
        // as FileTransferHostException rather than becoming an outcome the caller might ignore.
        var host = new ScriptedHost((request, _) => Refused(request, "That folder is read-only."));
        using var client = new FileTransferClient(host);

        var attempt = () => client.CopyRemoteAsync(
            "root-1", "in/report.pdf", "out/report.pdf", overwrite: false, null, CancellationToken.None);

        (await attempt.Should().ThrowAsync<FileTransferHostException>())
            .Which.HostMessage.Should().Be("That folder is read-only.");
    }

    [Fact]
    public async Task AMkdirRefusalThrowsEvenWhenItCarriesACode()
    {
        // REFUSAL-ONLY BY DESIGN. The host accepts no conflictResolution on mkdir, so a Replace or
        // Keep-both retry re-fails identically; returning an outcome here would invite a prompt the
        // user could never answer their way out of.
        var host = new ScriptedHost((request, _) =>
            Refused(request, "A folder with that name already exists.",
                FileTransferErrorCodes.DestinationExists, "Reports"));
        using var client = new FileTransferClient(host);

        var attempt = () => client.MakeDirectoryRemoteAsync("root-1", "", "Reports", CancellationToken.None);

        await attempt.Should().ThrowAsync<FileTransferHostException>();
    }

    // ─── The two converted throw sites (delete and rename) ────────────────────

    [Fact]
    public async Task ADeleteRefusalSurfacesAsTheTypedHostException()
    {
        // Both of these threw a bare IOException with a hardcoded English prefix, so the view
        // model's `catch (FileTransferHostException)` never saw them and the host's own advice was
        // replaced by a generic sentence — while nine sibling sites did the right thing.
        var host = new ScriptedHost((request, _) =>
            Refused(request, "Deleting is not allowed in this folder."));
        using var client = new FileTransferClient(host);

        var attempt = () => client.DeleteRemoteAsync("root-1", "in/report.pdf", CancellationToken.None);

        (await attempt.Should().ThrowAsync<FileTransferHostException>())
            .Which.HostMessage.Should().Be("Deleting is not allowed in this folder.");
        host.Sent.Should().ContainSingle().Which.Operation.Should().Be("delete");
    }

    [Fact]
    public async Task ARenameRefusalSurfacesAsTheTypedHostException()
    {
        var host = new ScriptedHost((request, _) => Refused(request, "That name is not allowed."));
        using var client = new FileTransferClient(host);

        var attempt = () => client.RenameRemoteAsync("root-1", "in/report.pdf", "new.pdf", CancellationToken.None);

        (await attempt.Should().ThrowAsync<FileTransferHostException>())
            .Which.HostMessage.Should().Be("That name is not allowed.");
        host.Sent.Should().ContainSingle().Which.Operation.Should().Be("rename");
    }

    [Fact]
    public async Task ABlankDeleteRefusalNeverPutsAStrandedPrefixOnScreen()
    {
        // THE CASE ForHostError EXISTS FOR, and the one the old code got visibly wrong: a host that
        // set errorMessage to the empty string produced the literal "Delete failed: " on the status
        // line — a colon, a space and nothing at all. A blank reply must become a plain IOException,
        // which the catch sites treat as "cause unknown" and answer with localized copy.
        var host = new ScriptedHost((request, _) => Refused(request, ""));
        using var client = new FileTransferClient(host);

        var attempt = () => client.DeleteRemoteAsync("root-1", "in/report.pdf", CancellationToken.None);

        var thrown = await attempt.Should().ThrowAsync<IOException>();
        thrown.Which.Should().NotBeOfType<FileTransferHostException>(
            "nothing the host said is fit to show, so nothing of the host's may be shown");
        thrown.Which.Message.Should().NotContain(
            "Delete failed: ", "that prefix was the bug, and it read as an unfinished sentence");
    }
}

/// <summary>
/// Which answers each of the host's codes may be offered, decided away from the view (RemEx-6vd8).
/// </summary>
/// <remarks>
/// The offer list is where a mistake costs a file, and none of it is visible in a screenshot of the
/// prompt: "Replace" against <c>destination_is_different_kind</c> means deleting a directory tree
/// to make room for one file, and against <c>resolved_name_taken</c> it overwrites the file the
/// user chose "keep both" specifically to protect.
/// </remarks>
public sealed class FileConflictPolicyTests
{
    [Fact]
    public void AnOrdinaryCollisionOffersKeepBothFirstAndReplaceSecond()
    {
        // ORDER IS ASSERTED, NOT JUST MEMBERSHIP. Position is a stronger recommendation than any
        // amount of de-emphasis — the first button is the one a hurried user presses — and the
        // Android review caught exactly this ordering putting the destructive answer at the top.
        FileConflictPolicy.ActionsFor(FileTransferErrorCodes.DestinationExists)
            .Should().ContainInOrder(
                FileConflictAction.KeepBoth,
                FileConflictAction.Replace,
                FileConflictAction.Skip,
                FileConflictAction.Cancel);
    }

    [Theory]
    [InlineData("destination_is_different_kind")]
    [InlineData("resolved_name_taken")]
    [InlineData("resolved_name_unusable")]
    [InlineData("some_code_this_build_has_never_heard_of")]
    public void ReplaceIsWithheldFromEveryCodeThatIsNotAPlainCollision(string errorCode)
    {
        FileConflictPolicy.ActionsFor(errorCode).Should().NotContain(FileConflictAction.Replace);
    }

    [Fact]
    public void ARenamedSiblingThatWasRacedCanStillBeAskedAboutAgain()
    {
        // The one refusal where asking again genuinely works: the host lost the name it invented to
        // another writer, so re-listing picks the next free one.
        FileConflictPolicy.ActionsFor(FileTransferErrorCodes.ResolvedNameTaken)
            .Should().Contain(FileConflictAction.KeepBoth);
    }

    [Fact]
    public void ANameTheDestinationCannotAcceptOffersNothingButStopping()
    {
        // Keep both was already tried and the destination refused the name it produced, so asking
        // the host to choose again from the same too-long stem cannot work.
        FileConflictPolicy.ActionsFor(FileTransferErrorCodes.ResolvedNameUnusable)
            .Should().Equal(FileConflictAction.Skip, FileConflictAction.Cancel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SomethingWithNoCodeIsNotACollisionAndGetsNoPrompt(string? errorCode)
    {
        // An ordinary failure has no answer a prompt could collect. Offering "Replace" for "the disk
        // is full" invites a retry that cannot work.
        FileConflictPolicy.ActionsFor(errorCode).Should().BeEmpty();
    }

    [Fact]
    public void EveryOfferedActionIsAllowedByThePromptThatOffersIt()
    {
        // ANTI-VACUITY: the sweep must actually have looked at something. A prompt whose Allows()
        // drifted from the policy would let a view deliver an answer the policy withheld.
        var prompt = new FileConflictPrompt(FileTransferErrorCodes.DestinationExists, "report.pdf");

        prompt.CanReplace.Should().BeTrue();
        prompt.CanKeepBoth.Should().BeTrue();
        prompt.Allows(FileConflictAction.Skip).Should().BeTrue();

        var differentKind = new FileConflictPrompt(FileTransferErrorCodes.DestinationIsDifferentKind, "Reports");
        differentKind.CanReplace.Should().BeFalse();
        differentKind.CanKeepBoth.Should().BeTrue();
    }

    [Fact]
    public void EachCodeGetsItsOwnExplanationEvenWhereTheActionsAreIdentical()
    {
        // resolved_name_unusable shares its ACTIONS with every unrecognised code but must not share
        // its SENTENCE: telling a user who has just chosen "keep both" that something unspecified
        // went wrong, when the truth is that the invented name was too long, is the moment the
        // feature stops being trustworthy.
        FileConflictPrompt.MessageKeyFor(FileTransferErrorCodes.ResolvedNameUnusable)
            .Should().NotBe(FileConflictPrompt.MessageKeyFor("a_code_from_a_newer_host"));

        var keys = new[]
        {
            FileConflictPrompt.MessageKeyFor(FileTransferErrorCodes.DestinationExists),
            FileConflictPrompt.MessageKeyFor(FileTransferErrorCodes.DestinationIsDifferentKind),
            FileConflictPrompt.MessageKeyFor(FileTransferErrorCodes.ResolvedNameTaken),
            FileConflictPrompt.MessageKeyFor(FileTransferErrorCodes.ResolvedNameUnusable),
            FileConflictPrompt.MessageKeyFor(null),
        };

        keys.Should().OnlyHaveUniqueItems();
    }
}
