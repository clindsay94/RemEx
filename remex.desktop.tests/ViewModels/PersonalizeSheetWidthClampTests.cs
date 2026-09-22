using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// RemEx-vkkcq: <see cref="ShellViewModel.ClampPersonalizeSheetWidth"/> is a static pure function
/// (min 440, max 60% of the shell width, floor of 440 when the shell width is not yet known), so it
/// is tested directly with no ViewModel construction needed.
/// </summary>
public class PersonalizeSheetWidthClampTests
{
    [Theory]
    [InlineData(300, 1920, 440)] // below the 440 floor
    [InlineData(700, 1920, 700)] // inside the range, passes through unchanged
    [InlineData(1500, 1920, 1152)] // above 60% of 1920 (1152), clamped down
    [InlineData(600, 0, 440)] // shell width not yet known (<= 0) - 440 floor only
    [InlineData(double.NaN, 1920, 440)] // NaN request guarded to the floor
    public void ClampPersonalizeSheetWidth_ClampsToTheExpectedBound(double requested, double shellWidth, double expected)
    {
        ShellViewModel.ClampPersonalizeSheetWidth(requested, shellWidth).Should().Be(expected);
    }
}
