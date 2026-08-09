using System.Linq;
using System.Reflection;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The PC-as-client tells a refusal nobody was asked for apart from one somebody decided
/// (RemEx-jc4q).
/// </summary>
/// <remarks>
/// The desktop is a client as well as a host and had the identical silence the phone had before
/// RemEx-3qmd: <c>ListVolumesAsync</c> dropped <c>denyReason</c>, so a peer's host refusing without
/// asking anybody produced "Full-device access was not granted." — true of both outcomes and useful
/// for only one of them.
/// </remarks>
public class VolumesResponseClassifierTests
{
    [Theory]
    [InlineData(true, null, null, VolumesOutcome.Granted)]
    [InlineData(false, null, null, VolumesOutcome.Refused)]
    [InlineData(false, "client_unreachable", null, VolumesOutcome.PeerUnreachable)]
    [InlineData(false, "something_new", null, VolumesOutcome.Refused)]
    [InlineData(false, null, "boom", VolumesOutcome.Failed)]
    public void ItClassifiesEachAnswer(bool granted, string? reason, string? error, VolumesOutcome expected)
        => VolumesResponseClassifier.Classify(granted, reason, error).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankReasonIsNotACode(string reason)
    {
        // A host that spells "no reason" as an empty string rather than by omitting the field must
        // not be read as having sent one. An ABSENT reason means a person decided — the contract
        // RemEx-l580 established — and reading it as unreachable would tell somebody to reconnect a
        // device that is working fine.
        VolumesResponseClassifier.Classify(false, reason, null).Should().Be(VolumesOutcome.Refused);
    }

    [Theory]
    [InlineData(true, "client_unreachable", "boom", VolumesOutcome.Failed)]
    [InlineData(true, "client_unreachable", null, VolumesOutcome.Granted)]
    public void ThePrecedenceHoldsWhenTheAnswersDisagree(
        bool granted, string reason, string? error, VolumesOutcome expected)
    {
        // ORDER, NOT PREFERENCE. errorMessage wins because the host sets it when the request never
        // got as far as asking anybody, and calling that a refusal blames the peer for a fault. A
        // grant already held never denies anything, so it outranks a reason code that should not
        // have been there. Each row is an arrangement where the wrong order gives a different answer.
        VolumesResponseClassifier.Classify(granted, reason, error).Should().Be(expected);
    }

    [Fact]
    public void EveryDenyReasonTheProtocolDeclaresGetsItsOwnWords()
    {
        // THE EXHAUSTIVENESS THE BEAD ASKED FOR. A new constant in remex.core is a new thing a peer
        // can say, and the failure mode of not handling it is silent: it falls into the generic
        // refusal and the user is told "not granted" for a reason the protocol went to the trouble of
        // naming. On the .NET side this is a straight reflection walk — no file parsing, unlike the
        // Kotlin half which has to read the C# as text.
        var declared = typeof(FileConsentDenyReasons)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        declared.Should().NotBeEmpty("a walk over nothing proves nothing");

        var unhandled = declared
            .Where(reason => VolumesResponseClassifier.Classify(false, reason, null) == VolumesOutcome.Refused)
            .ToArray();

        unhandled.Should().BeEmpty(
            "each of these is a reason the protocol names, so it deserves words of its own rather "
            + "than the generic refusal — add a VolumesOutcome and a string, or say here why not");
    }

    [Fact]
    public void TheUnreachableOutcomeHasWordsOfItsOwn()
    {
        // And the words exist: an outcome that maps to a missing resource key would throw or render
        // blank at the one moment the user needs telling something.
        var unreachable = LocalizationService.Instance["FileTransfer_VolumesPeerUnreachable"];
        var denied = LocalizationService.Instance["FileTransfer_VolumesDenied"];

        unreachable.Should().NotBeNullOrWhiteSpace();
        unreachable.Should().NotBe(denied, "the whole point is that these two read differently");
    }
}
