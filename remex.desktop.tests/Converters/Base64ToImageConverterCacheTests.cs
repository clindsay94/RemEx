using System;
using System.Globalization;
using FluentAssertions;
using Remex.Desktop.Converters;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// Perf audit P3-62: the converter used to decode a new, never-disposed bitmap on every binding
/// evaluation. It now decodes once per distinct string and hands back the same instance.
/// </summary>
public class Base64ToImageConverterCacheTests
{
    private static object? Convert(Base64ToImageConverter converter, object? value) =>
        converter.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    [Fact]
    public void TheSameStringIsDecodedOnceAndSharesOneResult()
    {
        var decodes = 0;
        var converter = new Base64ToImageConverter(_ => { decodes++; return new object(); });

        var first = Convert(converter, "aWNvbg==");
        var second = Convert(converter, "aWNvbg==");

        decodes.Should().Be(1);
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void DifferentStringsDecodeSeparately()
    {
        var decodes = 0;
        var converter = new Base64ToImageConverter(_ => { decodes++; return new object(); });

        Convert(converter, "YQ==").Should().NotBeSameAs(Convert(converter, "Yg=="));
        decodes.Should().Be(2);
    }

    [Fact]
    public void BlankOrNonStringValuesNeverDecode()
    {
        var decodes = 0;
        var converter = new Base64ToImageConverter(_ => { decodes++; return new object(); });

        Convert(converter, null).Should().BeNull();
        Convert(converter, "  ").Should().BeNull();
        Convert(converter, 42).Should().BeNull();
        decodes.Should().Be(0);
    }

    [Fact]
    public void TheCacheIsBoundedAndOversizedStringsAreNotCached()
    {
        var decodes = 0;
        var converter = new Base64ToImageConverter(_ => { decodes++; return new object(); });

        for (var i = 0; i <= Base64ToImageConverter.MaxCachedEntries; i++)
            Convert(converter, "k" + i);
        decodes.Should().Be(Base64ToImageConverter.MaxCachedEntries + 1);

        var huge = new string('A', Base64ToImageConverter.MaxCachedLength + 4);
        Convert(converter, huge);
        Convert(converter, huge);
        decodes.Should().Be(Base64ToImageConverter.MaxCachedEntries + 3, "a huge thumbnail is decoded per call, never held");
    }
}
