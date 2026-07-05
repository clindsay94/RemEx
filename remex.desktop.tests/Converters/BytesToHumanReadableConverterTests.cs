using System.Globalization;
using FluentAssertions;
using Remex.Desktop.Converters;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

public class BytesToHumanReadableConverterTests
{
    private readonly BytesToHumanReadableConverter _converter = BytesToHumanReadableConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    private string? Convert(object? value) =>
        _converter.Convert(value, typeof(string), null, _culture) as string;

    // ── Bytes ──────────────────────────────────────────────────────────────
    [Fact] public void Zero_Returns_ZeroB() => Convert(0L).Should().Be("0 B");
    [Fact] public void OneB_Returns_OneB() => Convert(1L).Should().Be("1 B");
    [Fact] public void MaxBytes_Returns_B() => Convert(1023L).Should().Be("1,023 B");

    // ── Kilobytes ──────────────────────────────────────────────────────────
    [Fact] public void ExactlyOneKB_Returns_KB() => Convert(1024L).Should().Be("1.0 KB");
    [Fact] public void HalfMB_Returns_KB() => Convert(512L * 1024).Should().Be("512.0 KB");
    [Fact] public void JustUnderOneMB_Returns_KB() => Convert(1024L * 1024 - 1).Should().Be("1024.0 KB");

    // ── Megabytes ──────────────────────────────────────────────────────────
    [Fact] public void ExactlyOneMB_Returns_MB() => Convert(1024L * 1024).Should().Be("1.0 MB");
    [Fact] public void HalfGB_Returns_MB() => Convert(512L * 1024 * 1024).Should().Be("512.0 MB");
    [Fact] public void JustUnderOneGB_Returns_MB() => Convert(1024L * 1024 * 1024 - 1).Should().Be("1024.0 MB");

    // ── Gigabytes ──────────────────────────────────────────────────────────
    [Fact] public void ExactlyOneGB_Returns_GB() => Convert(1024L * 1024 * 1024).Should().Be("1.0 GB");
    [Fact] public void EightGB_Returns_GB() => Convert(8L * 1024 * 1024 * 1024).Should().Be("8.0 GB");

    // ── Alternate input types ──────────────────────────────────────────────
    [Fact] public void Int_Input_Works() => Convert(1024).Should().Be("1.0 KB");
    [Fact] public void Null_Returns_Dash() => Convert(null).Should().Be("—");
    [Fact] public void Negative_Returns_Dash() => Convert(-1L).Should().Be("—");

    // ── ConvertBack not supported ──────────────────────────────────────────
    [Fact]
    public void ConvertBack_ThrowsNotSupported()
    {
        var act = () => _converter.ConvertBack("1.0 MB", typeof(long), null, _culture);
        act.Should().Throw<NotSupportedException>();
    }
}
