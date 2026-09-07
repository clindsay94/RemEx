using FluentAssertions;
using Remex.Desktop.Services;
using Remex.Desktop.Services.FileTransfer;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// <see cref="TransferProgressText"/> — the localized wording built on top of
/// <see cref="TransferProgressFormat"/>'s classification (RemEx-4lcq).
/// </summary>
/// <remarks>
/// Every test restores <see cref="LocalizationService"/>'s culture in a <c>finally</c>: it is a
/// process-lifetime singleton, so a test that left it on "fr" would leak into every test that runs
/// after it in the same process, including ones in other files.
/// </remarks>
public class TransferProgressTextTests
{
    private static void WithCulture(string culture, Action assert)
    {
        var original = LocalizationService.Instance.CultureTag;
        try
        {
            LocalizationService.Instance.SetCulture(culture);
            assert();
        }
        finally
        {
            LocalizationService.Instance.SetCulture(original);
        }
    }

    [Fact]
    public void RateText_Unknown_IsNull() =>
        TransferProgressText.RateText(new TransferRate.Unknown()).Should().BeNull();

    [Fact]
    public void RateText_BytesPerSecond_HasNoDecimal() =>
        WithCulture("en", () =>
            TransferProgressText.RateText(new TransferRate.Known(512, TransferRateUnit.BytesPerSecond))
                .Should().Be("512 B/s"));

    [Fact]
    public void RateText_MegabytesPerSecond_HasOneDecimal() =>
        WithCulture("en", () =>
            TransferProgressText.RateText(new TransferRate.Known(12.34, TransferRateUnit.MegabytesPerSecond))
                .Should().Be("12.3 MB/s"));

    /// <summary>
    /// The decimal separator follows RemEx's IN-APP language, not the OS locale — mixing the two is
    /// the exact bug this indirection exists to prevent (see the type's remarks).
    /// </summary>
    [Fact]
    public void RateText_UsesTheActiveLocalesDecimalSeparator() =>
        WithCulture("fr", () =>
            TransferProgressText.RateText(new TransferRate.Known(12.34, TransferRateUnit.MegabytesPerSecond))
                .Should().Be("12,3 MB/s"));

    [Fact]
    public void EtaText_Unknown_IsNull() =>
        TransferProgressText.EtaText(new TransferEta.Unknown()).Should().BeNull();

    [Fact]
    public void EtaText_Finishing_IsLocalized() =>
        WithCulture("en", () =>
            TransferProgressText.EtaText(new TransferEta.Finishing()).Should().Be("Finishing…"));

    [Theory]
    [InlineData(1, "1 second left")]
    [InlineData(2, "2 seconds left")]
    public void EtaText_Seconds_PicksThePluralCategory(int amount, string expected) =>
        WithCulture("en", () =>
            TransferProgressText.EtaText(new TransferEta.Remaining(amount, TransferEtaUnit.Seconds))
                .Should().Be(expected));

    /// <summary>
    /// Polish reaches all four categories through the real key lookup, not just PluralRules alone —
    /// proving the "_One/_Few/_Many/_Other" suffix is actually wired to the category.
    /// </summary>
    [Theory]
    [InlineData(1, "pozostała 1 sekunda")]
    [InlineData(3, "pozostały 3 sekundy")]
    [InlineData(5, "pozostało 5 sekund")]
    public void EtaText_Seconds_Polish_SelectsTheMatchingForm(int amount, string expected) =>
        WithCulture("pl", () =>
            TransferProgressText.EtaText(new TransferEta.Remaining(amount, TransferEtaUnit.Seconds))
                .Should().Be(expected));

    [Fact]
    public void ProgressSuffix_WithNoRate_IsNull() =>
        TransferProgressText.ProgressSuffix(new TransferRate.Unknown(), new TransferEta.Finishing())
            .Should().BeNull("a rate the caller has not established yet must not be masked by a lone ETA");

    [Fact]
    public void ProgressSuffix_WithRateButNoEta_IsTheRateAlone() =>
        WithCulture("en", () =>
            TransferProgressText.ProgressSuffix(new TransferRate.Known(1.0, TransferRateUnit.MegabytesPerSecond), new TransferEta.Unknown())
                .Should().Be("1.0 MB/s"));

    [Fact]
    public void ProgressSuffix_WithBoth_JoinsThem() =>
        WithCulture("en", () =>
            TransferProgressText.ProgressSuffix(
                    new TransferRate.Known(1.0, TransferRateUnit.MegabytesPerSecond),
                    new TransferEta.Remaining(3, TransferEtaUnit.Minutes))
                .Should().Be("1.0 MB/s · 3 minutes left"));
}
