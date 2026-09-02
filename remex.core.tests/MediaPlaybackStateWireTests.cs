using Remex.Core.Models;
using Remex.Core.Serialization;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins the artwork and timeline additions to the media wire types (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// TWO OPPOSITE MISTAKES ARE PINNED HERE, AND BOTH PRESENT AS SILENCE OR AS NOISE RATHER THAN AS AN
/// ERROR. The anchors must stay OFF the wire — they are host bookkeeping in a host clock's units, and
/// a phone that read them would be reading a timestamp from a machine whose clock it has never
/// agreed with — but they must stay IN value equality, because re-anchoring is the host noticing a
/// seek and a seek has to republish. Move them out of equality and a scrub goes unnoticed until the
/// track changes; leave them on the wire and every client parses a field it must not trust.
/// </para>
/// <para>
/// The position field is the counterpart: it IS on the wire and is null on every instance the
/// sampler compares, because a position that advances makes every poll unequal to the last and turns
/// a once-a-second read into a once-a-second broadcast to every connected phone. Nothing here can
/// prove the sampler leaves it null — that guard belongs with the sampler — but the shape that makes
/// it possible is fixed here.
/// </para>
/// </remarks>
public class MediaPlaybackStateWireTests
{
    private static string Serialize(MediaPlaybackState state)
        => RemexJson.Serialize(state, RemexJsonSerializerContext.Default.MediaPlaybackState);

    [Fact]
    public void ADifferentAnchorMakesADifferentState()
    {
        // THE POINT OF PUTTING THE ANCHORS IN EQUALITY. Same track, same status, same everything the
        // wire carries - and the host has just observed that playback jumped. If these compare equal
        // the republish never happens and the phone's progress bar keeps projecting from a position
        // the user seeked away from.
        var before = new MediaPlaybackState
        {
            Status = MediaPlaybackStatus.Playing,
            Title = "Blue in Green",
            AnchorPositionMs = 12_000,
            AnchorUtcMs = 1_700_000_000_000,
        };

        Assert.NotEqual(before, before with { AnchorPositionMs = 95_000 });
        Assert.NotEqual(before, before with { AnchorUtcMs = 1_700_000_030_000 });
    }

    [Fact]
    public void IdenticalAnchorsCompareEqual()
    {
        // The other half, and not a formality: an equality that is never equal republishes on every
        // tick just as surely as one that is always equal republishes never.
        var a = new MediaPlaybackState
        {
            Status = MediaPlaybackStatus.Playing,
            Title = "Blue in Green",
            ArtworkId = "0f1e2d3c4b5a6978",
            DurationMs = 337_000,
            AnchorPositionMs = 12_000,
            AnchorUtcMs = 1_700_000_000_000,
        };

        var b = a with { };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void TheWireCarriesTheTimelineAndNeverTheAnchors()
    {
        var json = Serialize(new MediaPlaybackState
        {
            Status = MediaPlaybackStatus.Playing,
            ArtworkId = "0f1e2d3c4b5a6978",
            DurationMs = 337_000,
            PositionMs = 12_500,
            AnchorPositionMs = 12_000,
            AnchorUtcMs = 1_700_000_000_000,
        });

        Assert.Contains("\"artworkId\":\"0f1e2d3c4b5a6978\"", json);
        Assert.Contains("\"durationMs\":337000", json);
        Assert.Contains("\"positionMs\":12500", json);

        // Case-insensitive, because the failure this guards against is someone dropping [JsonIgnore]
        // - which would emit "anchorPositionMs" - and a check that only knew one spelling would miss
        // a naming-policy change at the same time.
        Assert.DoesNotContain("anchor", json, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyTimelineIsAbsentRatherThanNull()
    {
        // WhenWritingNull is the existing policy and this feature has to live inside it: media_state
        // goes to every connected phone on every change, and four always-present nulls on a message
        // that mostly carries none of them is bytes spent on saying nothing.
        var json = Serialize(new MediaPlaybackState { Status = MediaPlaybackStatus.Paused });

        Assert.DoesNotContain("artworkId", json);
        Assert.DoesNotContain("durationMs", json);
        Assert.DoesNotContain("positionMs", json);
    }

    [Fact]
    public void AnEvictedArtworkAnswerOmitsTheImageRatherThanSendingAnEmptyOne()
    {
        // A NULL pngBase64 AND AN EMPTY ONE ARE DIFFERENT ANSWERS TO THE PHONE, which is the whole
        // reason the field is nullable. Absent means "the host no longer has that id, stop asking";
        // an empty string would decode to no bitmap and read as a corrupt image worth retrying.
        var json = RemexJson.Serialize(
            new MediaArtwork { ArtworkId = "0f1e2d3c4b5a6978" },
            RemexJsonSerializerContext.Default.MediaArtwork);

        Assert.Contains("\"artworkId\":\"0f1e2d3c4b5a6978\"", json);
        Assert.DoesNotContain("pngBase64", json);
    }

    [Fact]
    public void AnArtworkReplyCarriesItsBytesUntouched()
    {
        const string bytes = "iVBORw0KGgo=";

        var json = RemexJson.Serialize(
            new MediaArtwork { ArtworkId = "0f1e2d3c4b5a6978", PngBase64 = bytes },
            RemexJsonSerializerContext.Default.MediaArtwork);
        var round = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.MediaArtwork);

        Assert.NotNull(round);
        Assert.Equal(bytes, round.PngBase64);
    }

    [Fact]
    public void AnArtworkRequestRoundTripsThroughTheGeneratedContext()
    {
        // remex.core is NativeAOT, so a type missing from RemexJsonSerializerContext does not fail to
        // compile - it fails at runtime on the phone, in a build nobody runs the desktop tests
        // against. Serializing through the generated context here is what makes the registration a
        // compile-time fact rather than a review item.
        var json = RemexJson.Serialize(
            new MediaArtworkRequest { ArtworkId = "0f1e2d3c4b5a6978" },
            RemexJsonSerializerContext.Default.MediaArtworkRequest);

        Assert.Contains("\"artworkId\":\"0f1e2d3c4b5a6978\"", json);

        var round = RemexJson.Deserialize(json, RemexJsonSerializerContext.Default.MediaArtworkRequest);

        Assert.NotNull(round);
        Assert.Equal("0f1e2d3c4b5a6978", round.ArtworkId);
    }
}
