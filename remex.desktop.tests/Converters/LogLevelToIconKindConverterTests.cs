using FluentAssertions;
using Material.Icons;
using Microsoft.Extensions.Logging;
using Remex.Desktop.Converters;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// Guards <see cref="LogLevelToIconKindConverter"/>, the shape half of severity in the diagnostic log
/// list (RemEx-peagx). <see cref="LogLevelToBrushConverter"/> already covers colour; this converter is
/// what lets a colour-blind viewer, or a theme with poor warning/error contrast, still tell severities
/// apart by silhouette alone.
/// </summary>
public class LogLevelToIconKindConverterTests
{
    [Theory]
    [InlineData(LogLevel.Trace, MaterialIconKind.DotsHorizontal)]
    [InlineData(LogLevel.Debug, MaterialIconKind.BugOutline)]
    [InlineData(LogLevel.Information, MaterialIconKind.InformationOutline)]
    [InlineData(LogLevel.Warning, MaterialIconKind.AlertOutline)]
    [InlineData(LogLevel.Error, MaterialIconKind.CloseCircleOutline)]
    [InlineData(LogLevel.Critical, MaterialIconKind.AlertOctagonOutline)]
    public void EachLevel_MapsToItsIcon(LogLevel level, MaterialIconKind expected)
    {
        LogLevelToIconKindConverter.Instance
            .Convert(level, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }

    [Fact]
    public void UnknownValue_FallsBackToInformation()
    {
        LogLevelToIconKindConverter.Instance
            .Convert(null, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(MaterialIconKind.InformationOutline);
    }

    [Fact]
    public void WarningErrorAndCritical_AreVisuallyDistinctFromInformation()
    {
        // The acceptance criterion is that severity reads without colour: the alerting levels must
        // not collapse onto the same glyph as the everyday Information level.
        var info = LogLevelToIconKindConverter.Instance.Convert(
            LogLevel.Information, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture);
        var warning = LogLevelToIconKindConverter.Instance.Convert(
            LogLevel.Warning, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture);
        var error = LogLevelToIconKindConverter.Instance.Convert(
            LogLevel.Error, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture);
        var critical = LogLevelToIconKindConverter.Instance.Convert(
            LogLevel.Critical, typeof(MaterialIconKind), null, System.Globalization.CultureInfo.InvariantCulture);

        warning.Should().NotBe(info);
        error.Should().NotBe(info);
        critical.Should().NotBe(info);
        warning.Should().NotBe(error);
        error.Should().NotBe(critical);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        var act = () => LogLevelToIconKindConverter.Instance.ConvertBack(
            MaterialIconKind.InformationOutline, typeof(LogLevel), null, System.Globalization.CultureInfo.InvariantCulture);

        act.Should().Throw<NotSupportedException>();
    }
}
