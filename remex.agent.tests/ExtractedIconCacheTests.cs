using Remex.Agent.Services.Media;

namespace Remex.Agent.Tests;

/// <summary>
/// The artwork fallback's app-icon rung must not re-extract and re-encode the same icon on every
/// track change (perf audit P3-47).
/// </summary>
public class ExtractedIconCacheTests
{
    private static readonly DateTime Built = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SamePathAndStamp_ExtractsOnce()
    {
        var cache = new ExtractedIconCache(capacity: 8);
        var calls = 0;
        byte[]? Extract(string _) { calls++; return [1, 2, 3]; }

        var first = cache.GetOrAdd(@"C:\Apps\player.exe", Built, Extract);
        var second = cache.GetOrAdd(@"C:\Apps\player.exe", Built, Extract);

        Assert.Equal(1, calls);
        Assert.Same(first, second);
    }

    [Fact]
    public void AnUpdatedExecutable_IsExtractedAgain()
    {
        // Keyed on the last-write time as well as the path, so an app update's new icon shows up.
        var cache = new ExtractedIconCache(capacity: 8);
        var calls = 0;
        byte[]? Extract(string _) { calls++; return [(byte)calls]; }

        cache.GetOrAdd(@"C:\Apps\player.exe", Built, Extract);
        var updated = cache.GetOrAdd(@"C:\Apps\player.exe", Built.AddDays(1), Extract);

        Assert.Equal(2, calls);
        Assert.Equal(new byte[] { 2 }, updated);
    }

    [Fact]
    public void AMiss_IsCachedToo()
    {
        // The placeholder case: extracting again for the same file would miss again.
        var cache = new ExtractedIconCache(capacity: 8);
        var calls = 0;
        byte[]? Extract(string _) { calls++; return null; }

        Assert.Null(cache.GetOrAdd("/usr/share/applications/player.desktop", Built, Extract));
        Assert.Null(cache.GetOrAdd("/usr/share/applications/player.desktop", Built, Extract));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AMiss_IsRetriedOnceItsTtlExpires_AndAHitIsNot()
    {
        // The extractor reports "shell not ready yet" (explorer starting at logon) with the same
        // placeholder as "no icon", so a miss must not stick until the next app update.
        var now = Built;
        var cache = new ExtractedIconCache(capacity: 8, missTtl: TimeSpan.FromMinutes(5), utcNow: () => now);
        var missCalls = 0;
        var hitCalls = 0;
        byte[]? Miss(string _) { missCalls++; return missCalls == 1 ? null : [7]; }
        byte[]? Hit(string _) { hitCalls++; return [1]; }

        Assert.Null(cache.GetOrAdd("player.exe", Built, Miss));
        cache.GetOrAdd("other.exe", Built, Hit);

        now = Built.AddMinutes(4);
        Assert.Null(cache.GetOrAdd("player.exe", Built, Miss));   // still inside the TTL
        Assert.Equal(1, missCalls);

        now = Built.AddMinutes(6);
        Assert.Equal(new byte[] { 7 }, cache.GetOrAdd("player.exe", Built, Miss)); // retried, recovered
        Assert.Equal(2, missCalls);

        now = Built.AddDays(30);
        cache.GetOrAdd("other.exe", Built, Hit);
        cache.GetOrAdd("player.exe", Built, Miss);
        Assert.Equal(1, hitCalls);  // hits never expire
        Assert.Equal(2, missCalls); // and neither does the recovered one
    }

    [Fact]
    public void AThrownExtraction_IsNotCached()
    {
        var cache = new ExtractedIconCache(capacity: 8);
        var calls = 0;
        byte[]? Extract(string _)
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("shell not ready");
            return [9];
        }

        Assert.Throws<InvalidOperationException>(() => cache.GetOrAdd("player.exe", Built, Extract));
        Assert.Equal(0, cache.Count);
        Assert.Equal(new byte[] { 9 }, cache.GetOrAdd("player.exe", Built, Extract));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TheCacheStaysBounded()
    {
        var cache = new ExtractedIconCache(capacity: 4);
        for (var i = 0; i < 20; i++)
        {
            cache.GetOrAdd($"app{i}.exe", Built, _ => [1]);
        }

        Assert.InRange(cache.Count, 1, 4);
    }
}
