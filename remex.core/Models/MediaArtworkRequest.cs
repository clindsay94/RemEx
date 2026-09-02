namespace Remex.Core.Models;

/// <summary>
/// A client asking the host for the image behind one <see cref="MediaPlaybackState.ArtworkId"/>,
/// sent as <c>media_artwork_request</c> (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// CLIENT TO HOST, AND THE ONLY THING IT CAN ASK FOR IS AN ID THE HOST ALREADY MINTED. The id is a
/// content hash of bytes the host itself put in its artwork store, so this cannot be turned into a
/// "fetch me that file" primitive by a caller with a different string: an id the store has never
/// seen answers with an empty <see cref="MediaArtwork"/>, not with a lookup somewhere else.
/// </para>
/// <para>
/// PULL RATHER THAN PUSH, so a phone that already has the image never receives it twice and a phone
/// that is not showing the mini-player never receives it at all. Artwork is the largest thing on
/// this socket by two orders of magnitude; pushing it with every track change would spend that on
/// every connected client whether or not anyone is looking.
/// </para>
/// </remarks>
public sealed record MediaArtworkRequest
{
    /// <summary>The id from the <c>media_state</c> the client is holding.</summary>
    public string ArtworkId { get; init; } = string.Empty;
}
