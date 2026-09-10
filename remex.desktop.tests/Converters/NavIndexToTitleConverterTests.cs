using System.Globalization;
using FluentAssertions;
using Remex.Desktop.Converters;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Converters;

/// <summary>
/// Guards the ColorZone app bar's "current page" title (RemEx-a3prn) mapping the right drawer
/// destination index to the right, already-translated resource key.
/// </summary>
/// <remarks>
/// THE MAPPING IS OUT OF NUMERIC ORDER ON PURPOSE, and that is exactly why every index needs its
/// own case here (Opus review of 6522b12, MEDIUM 1). The drawer's own <c>ListBoxItem</c> Tags run
/// 0,1,2,3,4,7,8,9,6 - Files/Logs/Settings/About sit out of numeric sequence in the list - so a
/// mapping that only checked two or three indices could have <c>6</c> and <c>9</c> swapped
/// (About/Settings) and stay green. Nine cases, one per real destination, is what actually rules
/// that class of bug out rather than merely asserting the easy ones.
/// </remarks>
public class NavIndexToTitleConverterTests : IDisposable
{
    private readonly NavIndexToTitleConverter _converter = NavIndexToTitleConverter.Instance;
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;
    private readonly string _previousCultureTag;

    /// <summary>
    /// Pins <see cref="LocalizationService"/>'s ambient culture to English for the lifetime of this
    /// test class, restoring whatever it was on disposal (Opus review of 6522b12, LOW 2).
    /// </summary>
    /// <remarks>
    /// <see cref="NavIndexToTitleConverter.Convert"/> ignores the <see cref="CultureInfo"/> argument
    /// entirely and reads <c>LocalizationService.Instance</c>'s own ambient culture instead - the
    /// same singleton <c>HostPlatformLabelTests</c>, <c>FileTransferQueueTests</c> and
    /// <c>SystemStatusViewModelTests</c> all mutate. This suite was only ever safe because
    /// <c>AssemblyInfo.cs</c> disables test parallelization (RemEx-6s34) and those three restore in
    /// a <c>finally</c> - ambient safety, never asserted by this file itself. Pinning here makes the
    /// assumption explicit instead of inherited by accident.
    /// </remarks>
    public NavIndexToTitleConverterTests()
    {
        _previousCultureTag = LocalizationService.Instance.CultureTag;
        LocalizationService.Instance.SetCulture("en");
    }

    public void Dispose() => LocalizationService.Instance.SetCulture(_previousCultureTag);

    private object? Convert(object? value) =>
        _converter.Convert(value, typeof(string), null, _culture);

    [Theory]
    [InlineData(0, "Home")]
    [InlineData(1, "Sensors")]
    [InlineData(2, "Commands")]
    [InlineData(3, "Launcher")]
    [InlineData(4, "Processes")]
    [InlineData(5, "Remote Desktop")]
    [InlineData(6, "About")]
    [InlineData(7, "Files")]
    [InlineData(8, "Diagnostics")]
    [InlineData(9, "Settings")]
    public void KnownIndex_ReturnsTheSameLocalizedText_TheDrawerListItemUses(int index, string englishSubstring)
    {
        // Not asserting the exact resx string (that would duplicate Strings.resx and drift the
        // moment a translator edits it) - just that this converter reaches the SAME key the drawer's
        // own ListBoxItem already localizes with, by checking the English default contains the
        // destination name every other reader of that Nav_*/Shell_* key would recognize.
        var result = Convert(index) as string;

        result.Should().NotBeNullOrEmpty();
        result.Should().Contain(englishSubstring);
    }

    [Fact]
    public void Index5_RemoteDesktop_HasARealTitle_NotAnEmptyFallback()
    {
        // NavigateToRemoteDesktop (ShellViewModel) never sets IsShellChromeHidden - only
        // RemoteDesktopViewModel.ToggleFullScreen/NavigateBack do - so windowed remote desktop
        // shows this app bar with a real page to name. Nav_RemoteDesktop is a genuine new
        // user-facing string (all 9 locales), not a reuse of an existing drawer key, because no
        // existing key fit (Opus review of 6522b12, MEDIUM 2).
        Convert(5).Should().Be(LocalizationService.Instance["Nav_RemoteDesktop"]);
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
