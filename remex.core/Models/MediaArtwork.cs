namespace Remex.Core.Models;

/// <summary>
/// The host's answer to a <see cref="MediaArtworkRequest"/>, sent as <c>media_artwork</c>
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// THE ID COMES BACK WITH THE IMAGE BECAUSE THE ANSWER IS ASYNCHRONOUS. A phone can have asked for
/// two ids in the time one reply crosses the socket, and an image with no id attached is an image
/// the client has to guess a home for — which, on a track change, is how the wrong cover ends up
/// under the right title.
/// </para>
/// <para>
/// A MISSING <see cref="PngBase64"/> IS A REAL ANSWER, NOT A FAILURE. The host's store is a small
/// LRU, so an id from a track that scrolled out of it has genuinely gone; saying so lets the client
/// stop asking and draw its glyph, where silence would leave it retrying an id that will never
/// resolve.
/// </para>
/// <para>
/// THE BYTES ARE WHATEVER THE PLATFORM GAVE US — PNG from some sessions, JPEG from most SMTC
/// thumbnails and from remote <c>artUrl</c>s — and the host does not transcode. The field name is
/// frozen from the protocol design and is a misnomer for the JPEG case; that is a smaller cost than
/// a decode-and-re-encode of every cover on the host, and <c>BitmapFactory</c> on the phone sniffs
/// the format rather than trusting the name. Do not "fix" it by transcoding.
/// </para>
/// </remarks>
public sealed record MediaArtwork
{
    /// <summary>The id that was asked for, echoed back.</summary>
    public string ArtworkId { get; init; } = string.Empty;

    /// <summary>
    /// Base64 of the image bytes as the platform supplied them, or null when the host no longer has
    /// that id.
    /// </summary>
    public string? PngBase64 { get; init; }
}
