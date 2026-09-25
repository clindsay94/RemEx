using System;
using Remex.Agent.Services.Media;
using Remex.Core.Messages;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Perf audit P4-24: the media_artwork reply is serialized once per artwork, not per request.
/// </summary>
public class MediaArtworkReplyCacheTests
{
    private static byte[] Art(byte seed)
    {
        var bytes = new byte[4096];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void RepeatedRequests_ForTheSameArtwork_ShareOneSerializedReply()
    {
        var cache = new MediaArtworkReplyCache();
        var art = Art(1);
        var id = MediaArtworkStore.ComputeId(art);

        var first = cache.GetOrCreate(id, art);
        var second = cache.GetOrCreate(id, art);

        Assert.Same(first, second);
        Assert.Equal(1, cache.SerializeCount);
    }

    [Fact]
    public void Reply_IsByteIdentical_ToTheMessageItReplaces()
    {
        var cache = new MediaArtworkReplyCache();
        var art = Art(2);
        var id = MediaArtworkStore.ComputeId(art);

        var expected = MessageSerializer.Serialize(new RemexMessage
        {
            Type = MessageTypes.MediaArtwork,
            MediaArtwork = new Remex.Core.Models.MediaArtwork { ArtworkId = id, PngBase64 = Convert.ToBase64String(art) },
        });

        Assert.Equal(expected, cache.GetOrCreate(id, art));
    }

    [Fact]
    public void NewArtwork_OrAReStoredArray_IsSerializedAgain()
    {
        var cache = new MediaArtworkReplyCache();
        var a = Art(3);
        var b = Art(4);

        cache.GetOrCreate(MediaArtworkStore.ComputeId(a), a);
        var forB = cache.GetOrCreate(MediaArtworkStore.ComputeId(b), b);
        // Same content, new array: the store evicted and re-put it. Not trusted on the id alone.
        var aAgain = (byte[])a.Clone();
        var forAAgain = cache.GetOrCreate(MediaArtworkStore.ComputeId(aAgain), aAgain);

        Assert.Equal(3, cache.SerializeCount);
        var decoded = MessageSerializer.Deserialize(forB);
        Assert.Equal(Convert.ToBase64String(b), decoded!.MediaArtwork!.PngBase64);
        Assert.NotNull(MessageSerializer.Deserialize(forAAgain));
    }
}
