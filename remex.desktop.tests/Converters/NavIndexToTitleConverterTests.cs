using System.Globalization;
using FluentAssertions;
using Remex.Desktop.Converters;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// Guards the ColorZone app bar's "current page" title (RemEx-a3prn) mapping the right drawer
/// destination index to the right, already-translated resource key.
/// </summary>
public class NavIndexToTitleConverterTests
{
    private readonly NavIndexToTitleConverter _converter = NavIndexToTitleConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    private object? Convert(object? value) =>
        _converter.Convert(value, typeof(string), null, _culture);

    [Theory]
    [InlineData(0, "Home")]
    [InlineData(1, "Sensors")]
    public void KnownIndex_ReturnsTheSameLocalizedText_TheDrawerListItemUses(int index, string englishSubstring)
    {
        // Not asserting the exact resx string (that would duplicate Strings.resx and drift the
        // moment a translator edits it) - just that this converter reaches the SAME key the drawer's
        // own ListBoxItem already localizes with, by checking the English default contains the
        // destination name every other reader of Nav_Home/Nav_Sensors would recognize.
        var result = Convert(index) as string;

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain(englishSubstring);
    }

    [Fact]
    public void Index5_RemoteDesktop_ReturnsEmpty()
    {
        // No drawer entry maps to index 5 (RemoteDesktopViewModel is reached from inside the
        // Commands/Remote flow, not the nav list) and it is also the one page that hides this whole
        // app bar via IsShellChromeHidden. Falling back to empty rather than guessing a label keeps
        // this converter honest about not knowing one.
        Convert(5).Should().Be(string.Empty);
    }

    [Fact]
    public void OutOfRangeIndex_ReturnsEmpty_RatherThanThrowing()
    {
        Convert(999).Should().Be(string.Empty);
    }

    [Fact]
    public void NonIntValue_ReturnsEmpty_RatherThanThrowing()
    {
        Convert("not an index").Should().Be(string.Empty);
        Convert(null).Should().Be(string.Empty);
    }
}
