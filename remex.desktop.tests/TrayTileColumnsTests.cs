using Remex.Desktop.Converters;

namespace Remex.Desktop.Tests;

public class TrayTileColumnsTests
{
    [Theory]
    [InlineData(320, 2)]
    [InlineData(379, 2)]   // just below the first breakpoint
    [InlineData(380, 3)]   // exactly on it
    [InlineData(519, 3)]   // just below the second
    [InlineData(520, 4)]   // exactly on it
    [InlineData(1200, 4)]  // no fifth column, however wide
    public void Column_count_follows_the_breakpoints(double width, int expected)
    {
        Assert.Equal(expected, TrayTileColumnsConverter.ColumnsFor(width));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-50)]
    public void Degenerate_widths_fall_back_to_two_columns(double width)
    {
        // A UniformGrid with Columns = 0 lays every tile in a single row off the edge of the
        // window, so the fallback must never be zero. NaN is reachable: Bounds.Width is NaN
        // before the first layout pass.
        Assert.Equal(2, TrayTileColumnsConverter.ColumnsFor(width));
    }

    [Fact]
    public void Converter_delegates_to_ColumnsFor()
    {
        var result = TrayTileColumnsConverter.Instance.Convert(
            520d, typeof(int), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(4, result);
    }

    [Fact]
    public void Converter_given_a_non_double_falls_back_to_two_columns()
    {
        var result = TrayTileColumnsConverter.Instance.Convert(
            "not a width", typeof(int), null, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(2, result);
    }
}
