using System.Globalization;
using FluentAssertions;
using Remex.Client.Converters;
using Xunit;

namespace Remex.Client.Tests.Converters;

public class LocalizedFormatConverterTests
{
    private readonly LocalizedFormatConverter _converter = LocalizedFormatConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    private object? Convert(params object?[] values) =>
        _converter.Convert(values, typeof(string), null, _culture);

    [Fact]
    public void Formats_Template_With_Count() =>
        Convert("Sensors ({0})", 3).Should().Be("Sensors (3)");

    [Fact]
    public void Formats_Multiple_Args() =>
        Convert("{0} of {1}", 2, 5).Should().Be("2 of 5");

    [Fact]
    public void Template_Without_Placeholder_Returns_Template() =>
        Convert("Sensors", 3).Should().Be("Sensors");

    [Fact]
    public void Empty_Values_Returns_Empty() =>
        Convert().Should().Be(string.Empty);

    [Fact]
    public void NonString_First_Value_Returns_Empty() =>
        Convert(42, 3).Should().Be(string.Empty);

    [Fact]
    public void Malformed_Template_FallsBack_To_Template() =>
        Convert("Sensors ({0)", 3).Should().Be("Sensors ({0)");
}
