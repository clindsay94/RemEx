using System;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Material.Icons;
using Remex.Desktop.Converters;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// Guards <see cref="ActivityKindToIconKindConverter"/>, which replaced the emoji
/// <c>ActivityEntry.Glyph</c> used to compute for the Home "Recent activity" feed's leading icon
/// (RemEx-1ufoa.4).
/// </summary>
public class ActivityKindToIconKindConverterTests
{
    [Fact]
    public void EveryActivityKind_ConvertsToAKindOtherThanTheFallback()
    {
        var kinds = Enum.GetValues<ActivityKind>();
        kinds.Should().NotBeEmpty("a query that matches nothing asserts nothing");

        foreach (var kind in kinds)
        {
            var result = ActivityKindToIconKindConverter.Instance.Convert(
                kind, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture);

            result.Should().BeOfType<MaterialIconKind>();
            result.Should().NotBe(MaterialIconKind.CircleSmall,
                $"{kind} must map to a kind-specific icon, not the out-of-range fallback");
        }
    }

    [Fact]
    public void OutOfRangeValue_FallsBackToCircleSmall()
    {
        ActivityKindToIconKindConverter.Instance
            .Convert((ActivityKind)(-1), typeof(MaterialIconKind), null, CultureInfo.InvariantCulture)
            .Should().Be(MaterialIconKind.CircleSmall);
    }

    [Fact]
    public void NullValue_FallsBackToCircleSmall()
    {
        ActivityKindToIconKindConverter.Instance
            .Convert(null, typeof(MaterialIconKind), null, CultureInfo.InvariantCulture)
            .Should().Be(MaterialIconKind.CircleSmall);
    }

    [Fact]
    public void ConvertBack_Throws()
    {
        var act = () => ActivityKindToIconKindConverter.Instance.ConvertBack(
            MaterialIconKind.TrayArrowDown, typeof(ActivityKind), null, CultureInfo.InvariantCulture);

        act.Should().Throw<NotSupportedException>();
    }
}
