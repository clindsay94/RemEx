namespace Remex.Core.Models;

/// <summary>
/// A client asking the host to move the current track's playback position, sent as
/// <c>media_seek</c> (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// CLIENT TO HOST, AND THE ONLY THING IT CAN ASK FOR IS A POSITION IN THE TRACK THE HOST IS ALREADY
/// PLAYING. There is no track selector and no player selector: the host seeks whatever its own
/// session reader already reports, so this cannot be turned into "play something else" by a caller
/// with a different payload.
/// </para>
/// <para>
/// NOTHING IS SENT BACK, AND THAT IS THE DESIGN RATHER THAN AN OMISSION. The reply is the next
/// <c>media_state</c>: the sampler's following poll observes the moved position, the anchor tracker
/// re-anchors because the reading diverged past tolerance, and the gate publishes to every connected
/// client. A dedicated acknowledgement would say the seek was DISPATCHED, which is not the question
/// anyone is asking, and it would arrive before the position it was describing.
/// </para>
/// </remarks>
public sealed record MediaSeekRequest
{
    /// <summary>Where in the track to move to, in milliseconds from the start.</summary>
    /// <remarks>
    /// MILLISECONDS, LIKE EVERY OTHER POSITION ON THIS WIRE — <see cref="MediaPlaybackState.DurationMs"/>
    /// and <see cref="MediaPlaybackState.AnchorPositionMs"/> are the values a client computes this
    /// from. The platforms underneath disagree (SMTC counts 100-ns ticks, MPRIS counts microseconds)
    /// and each reader converts on its own side; a wire unit that matched one of them would make the
    /// other reader's conversion invisible to a reader of this file.
    /// </remarks>
    public long PositionMs { get; init; }
}
