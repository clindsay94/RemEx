using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Remex.Agent.Services.Media;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Covers the artwork fallback ladder — album art, then the app's icon, then nothing (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// THE ORDER IS THE DECISION AND THE SWALLOW IS THE CONTRACT. Spec 2.1 fixes the rungs; what these
/// tests protect is that a rung which throws is a rung that missed rather than a failure that escapes.
/// Every rung reaches outside the process — a WinRT thumbnail stream, a file, an HTTPS host, a
/// package manifest — and <c>IMediaArtworkSource</c> promises the sampler that none of that can take
/// it down.
/// </para>
/// <para>
/// EMPTY IS NOTHING, WHICH IS NOT PEDANTRY. A zero-byte thumbnail passed on would put an id in the
/// store for an image that cannot decode; the phone would fetch it, receive bytes, draw nothing, and
/// have no way to fall back to the glyph it would have drawn had the host simply said no.
/// </para>
/// </remarks>
public class MediaArtworkFallbackTests
{
    private static readonly byte[] Cover = [1, 2, 3, 4];
    private static readonly byte[] Icon = [9, 9, 9];

    [Fact]
    public async Task TheFirstAttemptThatProducesBytesWins()
    {
        var secondRan = false;

        var bytes = await MediaArtworkFallback.FirstNonEmptyAsync(
            [
                _ => Task.FromResult<byte[]?>(Cover),
                _ =>
                {
                    secondRan = true;
                    return Task.FromResult<byte[]?>(Icon);
                },
            ],
            CancellationToken.None);

        Assert.Same(Cover, bytes);

        // Not merely "the right bytes": the later rung must never have been STARTED. On Windows it
        // enumerates installed packages, which is not work to do once the cover is already in hand.
        Assert.False(secondRan);
    }

    [Fact]
    public async Task AnEmptyArrayCountsAsNothingAndTheNextRungIsTried()
    {
        var bytes = await MediaArtworkFallback.FirstNonEmptyAsync(
            [
                _ => Task.FromResult<byte[]?>([]),
                _ => Task.FromResult<byte[]?>(Icon),
            ],
            CancellationToken.None);

        Assert.Same(Icon, bytes);
    }

    [Fact]
    public async Task AThrowingAttemptIsSwallowedAndTheNextRungIsTried()
    {
        var bytes = await MediaArtworkFallback.FirstNonEmptyAsync(
            [
                _ => throw new IOException("the player closed its thumbnail stream"),
                _ => Task.FromResult<byte[]?>(Icon),
            ],
            CancellationToken.None);

        Assert.Same(Icon, bytes);
    }

    [Fact]
    public async Task AnAttemptThatFaultsItsTaskIsAlsoSwallowed()
    {
        // Throwing synchronously and returning a faulted task are different code paths through an
        // await, and a real fetch does the second far more often than the first.
        var bytes = await MediaArtworkFallback.FirstNonEmptyAsync(
            [
                _ => Task.FromException<byte[]?>(new UnauthorizedAccessException()),
                _ => Task.FromResult<byte[]?>(Icon),
            ],
            CancellationToken.None);

        Assert.Same(Icon, bytes);
    }

    [Fact]
    public async Task EveryRungMissingIsNullRatherThanAnException()
    {
        var bytes = await MediaArtworkFallback.FirstNonEmptyAsync(
            [
                _ => Task.FromResult<byte[]?>(null),
                _ => Task.FromResult<byte[]?>([]),
                _ => throw new InvalidOperationException(),
            ],
            CancellationToken.None);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task NoRungsAtAllIsNull()
    {
        Assert.Null(await MediaArtworkFallback.FirstNonEmptyAsync(
            new List<Func<CancellationToken, Task<byte[]?>>>(), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsTheOneThingThatEscapes()
    {
        // Shutdown must not be mistaken for a missing cover and swallowed into a null: the sampler
        // uses the cancellation to stop, and a ladder that ate it would keep walking rungs on the way
        // down.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MediaArtworkFallback.FirstNonEmptyAsync(
                [_ => Task.FromResult<byte[]?>(Cover)],
                cts.Token));
    }

    [Fact]
    public void TheIconExtractorsPlaceholderIsTreatedAsNoIcon()
    {
        // DesktopIconExtractionService never returns null — it returns a transparent placeholder PNG.
        // Passed on, that becomes an invisible cover on the phone that the user cannot tell from a
        // broken one, and the glyph they should have seen never draws.
        var missing = Path.Combine(Path.GetTempPath(), $"remex-no-such-app-{Guid.NewGuid():N}.exe");

        Assert.Null(MediaArtworkFallback.ExtractedIconBytes(missing));
    }
}
