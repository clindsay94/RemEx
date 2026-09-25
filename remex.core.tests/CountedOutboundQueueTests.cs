using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remex.Core.Native;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Covers the depth the legacy v2 transfer loops pace on (perf audit P4-4).
/// </summary>
/// <remarks>
/// The first cut read <c>ChannelReader.Count</c> on a SingleReader unbounded channel, which cannot
/// count and throws; a catch turned that into a permanent 0, so pacing never engaged. The Kotlin test
/// injected its own depth and passed. These run the real channel, so a depth that stops moving fails
/// here instead of in a phone's memory graph.
/// </remarks>
public class CountedOutboundQueueTests
{
    [Fact]
    public async Task Depth_TracksEnqueuesAndDequeues_BackToZero()
    {
        var queue = new CountedOutboundQueue<int>();
        Assert.Equal(0, queue.Depth);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(queue.TryEnqueue(i));
        }
        Assert.Equal(5, queue.Depth);

        await using var reader = queue.ReadAllAsync().GetAsyncEnumerator();
        for (int expectedDepth = 4; expectedDepth >= 0; expectedDepth--)
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(4 - expectedDepth, reader.Current);
            Assert.Equal(expectedDepth, queue.Depth);
        }

        // Refills from zero after a full drain: the counter has not drifted.
        Assert.True(queue.TryEnqueue(99));
        Assert.Equal(1, queue.Depth);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(99, reader.Current);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task Depth_ReturnsToZero_AfterConcurrentWritersAndOneReaderDrain()
    {
        const int writers = 8;
        const int perWriter = 500;
        var queue = new CountedOutboundQueue<int>();

        var received = new List<int>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var item in queue.ReadAllAsync())
            {
                received.Add(item);
            }
        });

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++)
            {
                Assert.True(queue.TryEnqueue(w * perWriter + i));
                Assert.True(queue.Depth >= 0);
            }
        })));

        queue.Complete();
        await readTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(writers * perWriter, received.Count);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public void FailedEnqueue_IsNotCounted()
    {
        var queue = new CountedOutboundQueue<int>();
        Assert.True(queue.TryEnqueue(1));
        queue.Complete();

        Assert.False(queue.TryEnqueue(2));
        Assert.Equal(1, queue.Depth);
    }
}
