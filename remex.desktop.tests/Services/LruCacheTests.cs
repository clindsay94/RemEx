using System.Collections.Generic;
using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Perf audit P0-20: the Remote Desktop cursor-shape cache was a dictionary keyed by the host's
/// ShapeSerial, which the host bumps on every cursor change, so it grew for the whole session. It is
/// now an <see cref="LruCache{TKey, TValue}"/> capped at
/// <see cref="RemoteDesktopViewModel.CursorShapeCacheCapacity"/>, and every value leaving it is handed
/// back so the view model can dispose its bitmap.
/// </summary>
public sealed class LruCacheTests
{
    [Fact]
    public void Never_exceeds_the_cursor_cache_cap_however_many_distinct_shapes_arrive()
    {
        var removed = new List<string>();
        var cache = new LruCache<long, string>(RemoteDesktopViewModel.CursorShapeCacheCapacity, removed.Add);

        for (long serial = 1; serial <= 1_000; serial++)
        {
            cache.Set(serial, $"shape{serial}");
            cache.Count.Should().BeLessThanOrEqualTo(RemoteDesktopViewModel.CursorShapeCacheCapacity);
        }

        cache.Count.Should().Be(32);
        removed.Should().HaveCount(1_000 - 32, "every evicted shape is handed back for disposal");
        cache.TryGetValue(1_000, out var newest).Should().BeTrue();
        newest.Should().Be("shape1000");
        cache.TryGetValue(1, out _).Should().BeFalse();
    }

    [Fact]
    public void Evicts_the_least_recently_used_entry()
    {
        var removed = new List<string>();
        var cache = new LruCache<long, string>(3, removed.Add);
        cache.Set(1, "a");
        cache.Set(2, "b");
        cache.Set(3, "c");

        // A cursor-state lookup touches 1, so 2 becomes the eldest.
        cache.TryGetValue(1, out _).Should().BeTrue();
        cache.Set(4, "d");

        removed.Should().Equal("b");
        cache.TryGetValue(2, out _).Should().BeFalse();
        cache.TryGetValue(1, out var a).Should().BeTrue();
        a.Should().Be("a");
    }

    [Fact]
    public void A_shape_the_cursor_keeps_selecting_survives_continuous_churn()
    {
        var cache = new LruCache<long, string>(4);
        cache.Set(7, "active");

        for (long serial = 100; serial <= 200; serial++)
        {
            cache.Set(serial, "other");
            cache.TryGetValue(7, out var active).Should().BeTrue();
            active.Should().Be("active");
        }

        cache.Count.Should().Be(4);
    }

    [Fact]
    public void Replacing_a_serial_hands_back_the_old_value_without_growing()
    {
        var removed = new List<string>();
        var cache = new LruCache<long, string>(2, removed.Add);
        cache.Set(1, "old");
        cache.Set(1, "new");

        cache.Count.Should().Be(1);
        removed.Should().Equal("old");
        cache.TryGetValue(1, out var value).Should().BeTrue();
        value.Should().Be("new");
    }

    [Fact]
    public void Re_setting_the_same_instance_does_not_hand_it_back()
    {
        var removed = new List<string>();
        var cache = new LruCache<long, string>(2, removed.Add);
        var shape = "shape";
        cache.Set(1, shape);
        cache.Set(1, shape);

        removed.Should().BeEmpty();
    }

    [Fact]
    public void Clear_drops_every_entry_and_hands_each_back()
    {
        var removed = new List<string>();
        var cache = new LruCache<long, string>(8, removed.Add);
        for (long serial = 1; serial <= 5; serial++)
        {
            cache.Set(serial, $"s{serial}");
        }

        cache.Clear();

        cache.Count.Should().Be(0);
        removed.Should().BeEquivalentTo(new[] { "s1", "s2", "s3", "s4", "s5" });
        cache.TryGetValue(3, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_non_positive_capacity()
    {
        var act = () => new LruCache<long, string>(0);
        act.Should().Throw<System.ArgumentOutOfRangeException>();
    }
}
