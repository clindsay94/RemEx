using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.RemoteDesktop;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Drives the raw-frame half of the encoder end to end — <see cref="FFmpegH264Encoder.EncodeFrame"/>
/// into the real bounded drop-write channel into the real stdin writer loop — against a recording
/// stream instead of a live ffmpeg child (RemEx-13se).
///
/// WHY THIS EXISTS. RemEx-lcp8 (pooled capture buffers) has been implemented and reverted three
/// times, and RemEx-wpk9 / RemEx-dfhq are blocked behind it. Every one of those failures has the
/// same shape: a buffer is recycled while ffmpeg is still reading it, which produces CORRUPTED VIDEO
/// rather than a crash or a red test. lcp8's third attempt proved the dangerous half of that — its
/// buffer pool's contract held perfectly under its own unit tests and still broke in production,
/// because nothing exercised the BOUNDARY between a caller that caches frames and a writer loop that
/// consumes them asynchronously. These tests are that boundary.
///
/// They are deliberately written against the CURRENT, non-pooling code and pass on it, so they pin
/// known-good behaviour first.
///
/// BE PRECISE ABOUT WHAT THIS DOES AND DOES NOT GUARANTEE — an earlier draft of this comment got it
/// wrong, and overclaiming here would recreate the exact false confidence the harness exists to end.
/// These tests are NOT an automatic gate on RemEx-lcp8. That bead's pool lives in the CAPTURE
/// BACKEND, which is not in this harness; no test below obtains its array from a pool, so a
/// capture-side pool could be introduced and these would all stay green.
///
/// What they actually are is two things, both narrower and both real:
///   • A pin on the encoder-side ownership CONTRACT — that EncodeFrame does not copy, that a full
///     channel discards frames with no reclaim hook, and that an aliased replay array is delivered
///     intact today. A change to any of those goes red.
///   • Reusable machinery — <c>RecordingStdin</c> and <c>AssertUniform</c> — that DOES detect a
///     buffer recycled mid-write. Proven by injection: a probe that submitted a single refilled
///     buffer repeatedly, and one that overwrote the replay array after submit, both failed loudly
///     with the offending byte index rather than passing quietly.
/// So an attempt at RemEx-lcp8 / RemEx-wpk9 / RemEx-dfhq must ROUTE ITS POOLED PRODUCER THROUGH THIS
/// HARNESS to get the benefit. Adding the pool without adding that test buys nothing here.
///
/// TWO KNOWN GAPS, both filed rather than merely mentioned:
///   • RemoteDesktopHandler is absent from the chain. It constructs its encoder directly
///     (RemoteDesktopHandler.cs:1793) with no injection seam, so putting the handler in the loop
///     would mean either spawning real ffmpeg or faking IH264Encoder — the first breaks the headless
///     constraint, the second removes the very code under test.
///   • Two concurrent /ws/desktop clients sharing the singleton IScreenCaptureService: RemEx-c5an.
/// </summary>
public sealed class FrameInputPipelineTests
{
    // Tiny frames: this fixture is about buffer lifetime, not pixels, and small frames keep the
    // drop-write behaviour in the last test quick and deterministic.
    private const int Width = 8;
    private const int Height = 4;
    private const int FrameBytes = Width * Height * 4;

    /// <summary>
    /// Stands in for ffmpeg's stdin. Records a COPY of every write so assertions inspect what
    /// actually arrived rather than aliasing the caller's array — a recorder that kept the reference
    /// would report whatever the producer last wrote and would prove nothing.
    /// </summary>
    private sealed class RecordingStdin : Stream
    {
        private readonly List<byte[]> _writes = new();
        private readonly object _gate = new();

        /// <summary>How long each write stays "in flight" before the bytes are read.</summary>
        public TimeSpan WriteDelay { get; init; } = TimeSpan.Zero;

        /// <summary>When set, every write blocks on this until released — used to fill the channel.</summary>
        public SemaphoreSlim? Hold { get; init; }

        public int WriteCount { get { lock (_gate) { return _writes.Count; } } }

        public IReadOnlyList<byte[]> Writes { get { lock (_gate) { return _writes.ToArray(); } } }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Hold is not null) await Hold.WaitAsync(cancellationToken);
            if (WriteDelay > TimeSpan.Zero) await Task.Delay(WriteDelay, cancellationToken);

            // The copy happens at the END of the write window ON PURPOSE. A real ffmpeg reads the
            // caller's memory across the whole write, so recording it last is what makes a producer
            // that recycles mid-write observable instead of silently tolerated.
            var copy = buffer.ToArray();
            lock (_gate) { _writes.Add(copy); }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Flush() { }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Frame(byte fill)
    {
        var frame = new byte[FrameBytes];
        frame.AsSpan().Fill(fill);
        return frame;
    }

    private static async Task WaitForWrites(RecordingStdin stdin, int count, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (stdin.WriteCount < count)
        {
            Assert.True(
                sw.ElapsedMilliseconds < timeoutMs,
                $"Timed out waiting for {count} write(s) to reach stdin; only {stdin.WriteCount} arrived.");
            await Task.Delay(10);
        }
    }

    private static void AssertUniform(byte[] actual, byte expected, string what)
    {
        Assert.Equal(FrameBytes, actual.Length);

        // Report the FIRST byte that disagrees rather than dumping the whole frame as noise.
        //
        // In practice a completed recycle shows up here as a uniform-but-WRONG frame rather than a
        // half-and-half one, because the recorder snapshots the memory in a single ToArray once the
        // write window closes. Detection is therefore by value, which is what the injection proof
        // actually exercised; genuine tearing would need the snapshot to race the overwrite, which
        // 128-byte frames make vanishingly unlikely. Either way this reports it.
        for (var i = 0; i < actual.Length; i++)
        {
            Assert.True(
                actual[i] == expected,
                $"{what}: byte {i} was 0x{actual[i]:X2}, expected 0x{expected:X2} throughout. " +
                "A partially-overwritten frame means something recycled the buffer while the write " +
                "was still in flight — see RemEx-lcp8.");
        }
    }

    [Fact]
    public async Task FramesArriveIntactAndInOrder()
    {
        var stdin = new RecordingStdin();
        using var encoder = new FFmpegH264Encoder(NullLogger.Instance);
        encoder.StartRawInputPipelineForTests(stdin, Width, Height);

        // One frame at a time, each awaited to the stream, so the capacity-3 drop-write channel
        // never discards anything and the assertion below can be exact rather than a subsequence.
        const int frameCount = 8;
        for (var i = 0; i < frameCount; i++)
        {
            encoder.EncodeFrame(Frame((byte)(i + 1)), forceKeyframe: false);
            await WaitForWrites(stdin, i + 1);
        }

        var writes = stdin.Writes;
        Assert.Equal(frameCount, writes.Count);
        for (var i = 0; i < frameCount; i++)
        {
            AssertUniform(writes[i], (byte)(i + 1), $"frame {i}");
        }
    }

    [Fact]
    public async Task EncodeFrameDoesNotCopy_SoTheCallerMustNotTouchTheBufferUntilTheWriteCompletes()
    {
        // THE CONTRACT ANY POOLING CHANGE HAS TO RESPECT, pinned as a fact rather than an argument.
        // EncodeFrame hands the memory to the channel as-is; ownership belongs to the writer loop
        // from a successful TryWrite until its WriteAsync returns. A producer that recycles inside
        // that window changes what ffmpeg receives.
        //
        // If a future change makes EncodeFrame copy, or adds a pool with correct lifetime, this test
        // SHOULD be updated — but as a deliberate decision (a copy costs a full memcpy per frame at
        // 120fps), never as an accident noticed after the fact.
        var stdin = new RecordingStdin { WriteDelay = TimeSpan.FromMilliseconds(150) };
        using var encoder = new FFmpegH264Encoder(NullLogger.Instance);
        encoder.StartRawInputPipelineForTests(stdin, Width, Height);

        var buffer = Frame(0xAA);
        encoder.EncodeFrame(buffer, forceKeyframe: false);

        // Recycle it while the write is still in flight — exactly what a buffer pool would do.
        buffer.AsSpan().Fill(0xBB);

        await WaitForWrites(stdin, 1);
        AssertUniform(
            stdin.Writes[0], 0xBB,
            "the frame that reached stdin after the caller recycled its buffer");
    }

    [Fact]
    public async Task AReplayedFrameIsDeliveredIntactBothTimes()
    {
        // THE EXACT SEQUENCE THAT BROKE RemEx-lcp8's THIRD ATTEMPT. On a static desktop the capture
        // backends do not allocate: DxgiDesktopCapture assigns _lastRawFrame once
        // (DxgiDesktopCapture.cs:659) and hands THE SAME REFERENCE out again on later ticks — it is
        // returned at :551, :577, :583, :599, :628, :637, :653 and :662. The :625-628 path is the
        // one that matters most here, because it replays the cached array when the scale still
        // matches and the caller has already computed isLive=true for it (:537-538), so the handler
        // submits it as a real frame. One array can therefore sit in the pipeline twice.
        //
        // (RemEx-13se's own description carries an older, wrong set of line numbers for this; these
        // were re-read from the file.)
        //
        // Today that aliasing is harmless read-only sharing with GC-managed lifetime, which is why
        // both deliveries below are intact. A pool that reclaims the array after the first write
        // makes the second one a read of a recycled buffer, and this test goes red.
        var stdin = new RecordingStdin();
        using var encoder = new FFmpegH264Encoder(NullLogger.Instance);
        encoder.StartRawInputPipelineForTests(stdin, Width, Height);

        var replayed = Frame(0x5A);

        encoder.EncodeFrame(replayed, forceKeyframe: false);
        await WaitForWrites(stdin, 1);
        encoder.EncodeFrame(replayed, forceKeyframe: false);
        await WaitForWrites(stdin, 2);

        AssertUniform(stdin.Writes[0], 0x5A, "first delivery of the replayed frame");
        AssertUniform(stdin.Writes[1], 0x5A, "second delivery of the same replayed array");
    }

    [Fact]
    public async Task AFullInputChannelDiscardsFramesWithoutTellingTheProducer()
    {
        // WHY THIS MATTERS TO THE POOLING BEADS. The input channel is FullMode=DropWrite, so
        // TryWrite returns TRUE and silently discards the frame when the channel is full. A producer
        // therefore cannot learn that its buffer was dropped, which means a "the writer loop returns
        // the buffer" ownership scheme leaks on exactly the busy path — the normal backpressure
        // case, not an error case. RemEx-wpk9 records the same defect on the output channel.
        using var hold = new SemaphoreSlim(0);
        var stdin = new RecordingStdin { Hold = hold };
        using var encoder = new FFmpegH264Encoder(NullLogger.Instance);
        encoder.StartRawInputPipelineForTests(stdin, Width, Height);

        // Every write blocks, so the channel backs up and starts dropping.
        const int submitted = 12;
        for (var i = 0; i < submitted; i++)
        {
            encoder.EncodeFrame(Frame((byte)(i + 1)), forceKeyframe: false);
        }

        // The encoder believes it accepted all of them: the counter is incremented on TryWrite's
        // return value, and DropWrite makes that return value a lie.
        Assert.Equal(submitted, encoder.AcceptedInputFrameCount);

        hold.Release(submitted);

        // Let the writer drain whatever actually survived. There is no event to await here — that is
        // the point of the test — so wait for the first write to land BEFORE looking for a plateau.
        // Seeding the plateau loop straight away would let one slow 100ms tick on a loaded machine
        // read zero writes as "drained" and fail the InRange below for the wrong reason.
        await WaitForWrites(stdin, 1);

        var sw = Stopwatch.StartNew();
        var last = -1;
        while (sw.ElapsedMilliseconds < 2_000 && stdin.WriteCount != last)
        {
            last = stdin.WriteCount;
            await Task.Delay(100);
        }

        // InRange rather than `delivered < submitted`, which would also pass at zero deliveries and
        // so could go green for the wrong reason on a slow machine. The writer blocks inside its
        // FIRST WriteAsync (Hold starts at 0 permits) having taken one frame off the channel, so at
        // most capacity + that one in-flight frame can ever survive, and at least one must.
        var delivered = stdin.WriteCount;
        Assert.InRange(delivered, 1, FFmpegH264Encoder.InputChannelCapacity + 1);
        Assert.True(
            delivered < submitted,
            $"Expected the capacity-{FFmpegH264Encoder.InputChannelCapacity} drop-write channel to " +
            $"discard frames under a stalled writer, but all {submitted} reached stdin. If the " +
            "channel's FullMode changed, the ownership analysis in RemEx-lcp8 and RemEx-wpk9 needs " +
            "revisiting.");

        // Whatever did survive must still be whole — dropping frames is the design, tearing them is not.
        var writes = stdin.Writes;
        for (var i = 0; i < writes.Count; i++)
        {
            Assert.Equal(FrameBytes, writes[i].Length);
            Assert.True(
                writes[i].All(b => b == writes[i][0]),
                $"Delivered frame {i} was torn: a dropped frame must vanish whole, never partially.");
        }
    }
}
