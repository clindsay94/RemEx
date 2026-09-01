using System.Globalization;
using FluentAssertions;
using Remex.Desktop.Converters;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

public class MultiplyConverterTests
{
    private readonly MultiplyConverter _converter = MultiplyConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    private object? Convert(object? value, object? parameter) =>
        _converter.Convert(value, typeof(double), parameter, _culture);

    // ── Mica ceiling (0.30) ──────────────────────────────────────────────────
    [Fact] public void Mica_ClearFloor_ScalesDown() =>
        Convert(0.01, "0.30").Should().Be(0.003);

    [Fact] public void Mica_FrostedCeiling_ReproducesOldFixedValue() =>
        Convert(1.0, "0.30").Should().Be(0.30);

    [Fact] public void Mica_Midpoint_ScalesLinearly() =>
        Convert(0.5, "0.30").Should().Be(0.15);

    // ── Acrylic ceiling (0.25) ───────────────────────────────────────────────
    [Fact] public void Acrylic_ClearFloor_ScalesDown() =>
        Convert(0.01, "0.25").Should().Be(0.0025);

    [Fact] public void Acrylic_FrostedCeiling_ReproducesOldFixedValue() =>
        Convert(1.0, "0.25").Should().Be(0.25);

    // ── Clamping ─────────────────────────────────────────────────────────────
    [Fact] public void ResultAboveOne_ClampsToOne() =>
        Convert(2.0, "1.0").Should().Be(1.0);

    [Fact] public void NegativeResult_ClampsToZero() =>
        Convert(-1.0, "0.30").Should().Be(0.0);

    // ── Bad input ────────────────────────────────────────────────────────────
    [Fact] public void NonDoubleValue_ReturnsZero() =>
        Convert("not a double", "0.30").Should().Be(0.0);

    [Fact] public void NullParameter_ReturnsZero() =>
        Convert(1.0, null).Should().Be(0.0);

    [Fact] public void UnparseableParameter_ReturnsZero() =>
        Convert(1.0, "not a number").Should().Be(0.0);

    // ── ConvertBack not supported ───────────────────────────────────────────
    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        var act = () => _converter.ConvertBack(0.15, typeof(double), "0.30", _culture);
        act.Should().Throw<NotSupportedException>();
    }
}
