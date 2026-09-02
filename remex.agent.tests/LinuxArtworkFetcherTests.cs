using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Remex.Agent.Services.Media;
using Tmds.DBus.Protocol;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Covers the policy around <c>mpris:artUrl</c> — the one outbound request the agent makes
/// (RemEx-vtorl).
/// </summary>
/// <remarks>
/// <para>
/// AN <c>artUrl</c> IS AN ARBITRARY STRING FROM ANOTHER PROCESS. Whatever media player the user has
/// open puts it there, so the question is never "is this URL fine" but "is it one of the two shapes
/// that were approved": <c>file://</c> read from disk, and <c>https://</c> fetched under a 5 s / 2 MB
/// / no-redirect budget. Spec 2.1 approved the HTTPS rung on 2026-09-02 because Spotify, Apple Music
/// and browsers publish nothing else, and it says to keep it the only outbound request in the agent.
/// </para>
/// <para>
/// NOTHING HERE TOUCHES THE NETWORK. Every rejection below is decided before a request is made, which
/// is exactly the property worth testing — a test that had to observe a connection failing would pass
/// just as happily against an implementation that tried <c>http://</c> first and gave up.
/// </para>
/// </remarks>
public class LinuxArtworkFetcherTests
{
    [Theory]
    [InlineData("http://example.invalid/cover.jpg")]
    [InlineData("HTTP://example.invalid/cover.jpg")]
    [InlineData("ftp://example.invalid/cover.jpg")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("cover.png")]
    [InlineData("../art/cover.png")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task OnlyFileAndHttpsAreEverFetched(string? artUrl)
    {
        // Plaintext http is excluded so a hostile player cannot use the agent as a cleartext beacon;
        // a relative string is excluded because there is no base URI that would be correct — the
        // metadata came from another process, not from a document.
        Assert.Null(await LinuxArtworkFetcher.FetchAsync(artUrl, CancellationToken.None));
    }

    [Fact]
    public async Task AFileUrlPointingAtNothingIsNull()
    {
        // The common case, not an edge one: most players publish a path into their own cache
        // directory and clear it out behind them.
        var missing = Path.Combine(Path.GetTempPath(), $"remex-art-{Guid.NewGuid():N}.png");

        Assert.Null(await LinuxArtworkFetcher.FetchAsync(new Uri(missing).AbsoluteUri, CancellationToken.None));
    }

    [Fact]
    public async Task AFileUrlWithinTheCapIsRead()
    {
        // The positive control. Without it, a fetcher that returned null unconditionally would pass
        // every other test in this file.
        var path = Path.Combine(Path.GetTempPath(), $"remex-art-{Guid.NewGuid():N}.png");
        var expected = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        await File.WriteAllBytesAsync(path, expected);

        try
        {
            var bytes = await LinuxArtworkFetcher.FetchAsync(new Uri(path).AbsoluteUri, CancellationToken.None);

            Assert.NotNull(bytes);
            Assert.Equal(expected, bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFileUrlOverTheCapIsNull()
    {
        // Art the store would refuse is art not worth reading: MediaArtworkStore drops anything past
        // the same 2 MB, so accepting it here would only mean spending the memory twice before
        // arriving at the same answer.
        var path = Path.Combine(Path.GetTempPath(), $"remex-art-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[LinuxArtworkFetcher.MaxBytes + 1]);

        try
        {
            Assert.Null(await LinuxArtworkFetcher.FetchAsync(new Uri(path).AbsoluteUri, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnEmptyFileIsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"remex-art-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, []);

        try
        {
            Assert.Null(await LinuxArtworkFetcher.FetchAsync(new Uri(path).AbsoluteUri, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TheBoundedCopyRefusesABodyOneByteOverTheCap()
    {
        // The HTTPS rung cannot trust Content-Length — a header is a claim and the body is the fact —
        // so the cap is enforced while copying. One byte over must be a refusal and not a truncation:
        // a truncated image decodes to a half-drawn cover the phone has no way to recognise as
        // broken.
        using var source = new MemoryStream(new byte[1025]);

        Assert.Null(await LinuxArtworkFetcher.ReadBoundedAsync(source, 1024, CancellationToken.None));
    }

    [Fact]
    public async Task TheBoundedCopyAcceptsABodyExactlyAtTheCap()
    {
        using var source = new MemoryStream(new byte[1024]);

        var bytes = await LinuxArtworkFetcher.ReadBoundedAsync(source, 1024, CancellationToken.None);

        Assert.NotNull(bytes);
        Assert.Equal(1024, bytes.Length);
    }

    [Fact]
    public async Task TheBoundedCopyTreatsAnEmptyBodyAsNothing()
    {
        using var source = new MemoryStream([]);

        Assert.Null(await LinuxArtworkFetcher.ReadBoundedAsync(source, 1024, CancellationToken.None));
    }
}

/// <summary>
/// Covers <see cref="LinuxMediaSessionReader.TryGetInt64"/>, the numeric-shape fallback that runs
/// twice per player per poll tick for <c>mpris:length</c> and <c>Position</c> (RemEx-vtorl).
/// </summary>
/// <remarks>
/// MPRIS SAYS INT64 AND PLAYERS DISAGREE — some publish <c>uint64</c>, others a <c>double</c>. The
/// switch on <see cref="Tmds.DBus.Protocol.VariantValue.Type"/> must cover every shape a real player
/// ships without throwing, because the previous try/catch chain cost real exceptions on every tick
/// for a player that did not publish int64.
/// </remarks>
public class LinuxMediaSessionReaderTryGetInt64Tests
{
    [Fact]
    public void Int64IsReadDirectly() =>
        Assert.Equal(123456789L, LinuxMediaSessionReader.TryGetInt64((VariantValue)123456789L));

    [Fact]
    public void UInt64IsAccepted() =>
        Assert.Equal(987654321L, LinuxMediaSessionReader.TryGetInt64((VariantValue)987654321UL));

    [Fact]
    public void Int32IsAccepted() =>
        Assert.Equal(42L, LinuxMediaSessionReader.TryGetInt64((VariantValue)42));

    [Fact]
    public void DoubleIsTruncatedToLong() =>
        Assert.Equal(1500L, LinuxMediaSessionReader.TryGetInt64((VariantValue)1500.75));

    [Fact]
    public void StringIsNotANumber() =>
        Assert.Null(LinuxMediaSessionReader.TryGetInt64((VariantValue)"not-a-number"));
}
