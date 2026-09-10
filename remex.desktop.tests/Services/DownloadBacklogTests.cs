using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Covers the download backlog ceiling that stops a slow destination consuming unbounded memory
/// (RemEx-gyf4).
/// </summary>
/// <remarks>
/// <para>
/// A download hands every arriving chunk to a channel drained by one writer task, which is what
/// keeps the file in order. Nothing in the protocol lets the receiver slow the sender: the host
/// streams chunks and expects no per-chunk reply. So when the destination writes slower than the
/// network delivers, the channel accumulates the entire difference — for a large file onto a slow
/// USB stick, gigabytes of managed arrays.
/// </para>
/// <para>
/// The obvious fix, a bounded channel, is the wrong one and these tests exist partly to record why:
/// the producer runs on the connection's SYNCHRONOUS dispatch, so there is nowhere to await room,
/// and a bounded <c>TryWrite</c> would return false and silently DROP the chunk — a corrupt file,
/// surfaced later as an integrity failure that blames the wrong thing.
/// </para>
/// </remarks>
public class DownloadBacklogTests
{
    private const long Limit = 1000;

    private static StrongBox<long> Backlog(long queued = 0) => new(queued);

    [Fact]
    public void AcceptsChunksWhileTheBacklogFitsUnderTheCeiling()
    {
        // Positive control. Every assertion below would also pass against a helper that refused
        // everything, which would abandon every download on its first chunk.
        var backlog = Backlog();

        FileTransferClient.TryReserveBacklog(backlog, 400, Limit, out _).Should().BeTrue();
        FileTransferClient.TryReserveBacklog(backlog, 400, Limit, out _).Should().BeTrue();

        backlog.Value.Should().Be(800);
    }

    [Fact]
    public void AcceptsAChunkThatLandsExactlyOnTheCeiling()
    {
        var backlog = Backlog(600);

        FileTransferClient.TryReserveBacklog(backlog, 400, Limit, out _).Should().BeTrue();

        backlog.Value.Should().Be(Limit);
    }

    [Fact]
    public void RefusesTheChunkThatWouldCrossTheCeiling()
    {
        var backlog = Backlog(900);

        FileTransferClient.TryReserveBacklog(backlog, 200, Limit, out var wouldQueue).Should().BeFalse();

        wouldQueue.Should().Be(1100, "the caller reports what the backlog would have reached, not what it is");
    }

    [Fact]
    public void ARefusedChunkLeavesTheBacklogExactlyAsItWas()
    {
        // THE POINT OF THE HELPER. The counter is bumped optimistically because acceptance is the
        // common path, so a rejection must roll it back. Without the rollback every refused chunk
        // permanently consumes a slice of the ceiling, and a transfer whose disk catches up is still
        // abandoned later — on a backlog that no longer exists.
        var backlog = Backlog(900);

        FileTransferClient.TryReserveBacklog(backlog, 200, Limit, out _).Should().BeFalse();

        backlog.Value.Should().Be(900);
    }

    [Fact]
    public void RepeatedRefusalsDoNotDriftTheCounter()
    {
        // The realistic shape of the failure: chunks keep arriving after the ceiling is hit, because
        // the host has no idea anything is wrong and will not stop until the transfer is torn down.
        var backlog = Backlog(950);

        for (int i = 0; i < 100; i++)
        {
            FileTransferClient.TryReserveBacklog(backlog, 100, Limit, out _).Should().BeFalse();
        }

        backlog.Value.Should().Be(950);
    }

    [Fact]
    public void TheWriterRetiringBytesLetsAStalledTransferRecover()
    {
        // A transient stall must not be fatal. Once the writer drains, the same chunk that was
        // refused a moment ago has to be accepted — otherwise the ceiling is a one-way trip and any
        // momentary disk hiccup kills the download.
        var backlog = Backlog(950);
        FileTransferClient.TryReserveBacklog(backlog, 100, Limit, out _).Should().BeFalse();

        System.Threading.Interlocked.Add(ref backlog.Value, -900); // writer catches up

        FileTransferClient.TryReserveBacklog(backlog, 100, Limit, out _).Should().BeTrue();
        backlog.Value.Should().Be(150);
    }

    [Fact]
    public async Task ConcurrentReservationsNeverExceedTheCeiling()
    {
        // The producer is single-threaded today, but the counter is shared with the writer task that
        // decrements it, so the accounting has to be atomic rather than merely ordered. A read-then-
        // write implementation passes every test above and fails this one.
        var backlog = Backlog();
        var accepted = new List<bool>();
        var gate = new object();

        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
        {
            bool ok = FileTransferClient.TryReserveBacklog(backlog, 100, Limit, out _);
            lock (gate) { accepted.Add(ok); }
        })));

        backlog.Value.Should().BeLessThanOrEqualTo(Limit);
        accepted.FindAll(a => a).Count.Should().Be(10, "only ten 100-byte chunks fit under a 1000-byte ceiling");
    }

    [Fact]
    public void RetiringWrittenBytesReleasesTheCeilingAgain()
    {
        // The half the first version of these tests could not reach. Retirement was inline in the
        // writer loop, so DELETING IT left all eight tests green while every download past the
        // ceiling failed — a line with no coverage guarding the entire feature. This covers the
        // FUNCTION, not the writer's call to it: deleting that call still leaves all ten green, and
        // closing that needs a seam the client does not have (RemEx-wfim).
        var backlog = Backlog();
        FileTransferClient.TryReserveBacklog(backlog, 1000, Limit, out _).Should().BeTrue();
        FileTransferClient.TryReserveBacklog(backlog, 1, Limit, out _).Should().BeFalse();

        FileTransferClient.RetireBacklog(backlog, 1000);

        backlog.Value.Should().Be(0);
        FileTransferClient.TryReserveBacklog(backlog, 1000, Limit, out _)
            .Should().BeTrue("a fully drained writer must hand the whole ceiling back");
    }

    [Fact]
    public void ReserveAndRetireAreExactInverses()
    {
        // A steady transfer is thousands of these pairs. Any asymmetry — an off-by-one, a signed
        // slip — accumulates silently until a long download dies on a backlog that was never real.
        var backlog = Backlog();

        for (int i = 0; i < 5000; i++)
        {
            FileTransferClient.TryReserveBacklog(backlog, 64, Limit, out _).Should().BeTrue();
            FileTransferClient.RetireBacklog(backlog, 64);
        }

        backlog.Value.Should().Be(0);
    }

    [Fact]
    public void TheShippedCeilingIsGenerousEnoughToAbsorbARealStall()
    {
        // Guards the constant against being "tidied" down to something that fires in normal use. A
        // destination pausing for a full second at 100 MB/s must comfortably fit, or transfers start
        // failing on ordinary disk hiccups.
        const long oneSecondAtHundredMegabytesPerSecond = 100L * 1024 * 1024;

        FileTransferClient.MaxQueuedDownloadBytes.Should().BeGreaterThan(oneSecondAtHundredMegabytesPerSecond);
    }
}
