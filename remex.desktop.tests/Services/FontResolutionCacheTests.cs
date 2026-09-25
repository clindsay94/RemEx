using System;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Perf audit P3-64: every theme apply re-probed each typography font's glyph typeface, including the
/// 1.6 MB Nabla asset. The probe answer is now kept per font string; a platform that is not ready yet
/// is not an answer and is never cached.
/// </summary>
public class FontResolutionCacheTests
{
    [Fact]
    public void AResolvedFontIsProbedOnceAndTheSameFamilyIsReturned()
    {
        var probes = 0;
        var cache = new FontResolutionCache(value => { probes++; return new FontFamily(value); });

        cache.TryResolve("Inter", out var first).Should().BeTrue();
        cache.TryResolve("Inter", out var second).Should().BeTrue();

        probes.Should().Be(1);
        second.Should().BeSameAs(first, "a stable instance keeps SetOwnResourceIfChanged's comparison trivially equal");
    }

    [Fact]
    public void AnUnresolvableFontIsRememberedAsUnresolvable()
    {
        var probes = 0;
        var cache = new FontResolutionCache(_ => { probes++; return null; });

        cache.TryResolve("avares://Nowhere/Assets#Missing", out var family).Should().BeFalse();
        cache.TryResolve("avares://Nowhere/Assets#Missing", out _).Should().BeFalse();

        probes.Should().Be(1);
        family.Should().Be(FontFamily.Default);
    }

    [Fact]
    public void AProbeThatThrowsIsNotCached()
    {
        // FontManager.Current throws before the platform is initialised; caching that as "unresolvable"
        // would pin the user's font to the fallback for the rest of the process.
        var probes = 0;
        var cache = new FontResolutionCache(value =>
        {
            probes++;
            if (probes == 1) throw new InvalidOperationException("no platform yet");
            return new FontFamily(value);
        });

        cache.Invoking(c => c.TryResolve("Inter", out _)).Should().Throw<InvalidOperationException>();
        cache.TryResolve("Inter", out _).Should().BeTrue();
        probes.Should().Be(2);
    }

    [Fact]
    public void BlankValuesNeverProbe()
    {
        var probes = 0;
        var cache = new FontResolutionCache(_ => { probes++; return null; });

        cache.TryResolve(null, out _).Should().BeFalse();
        cache.TryResolve("  ", out _).Should().BeFalse();

        probes.Should().Be(0);
    }
}
