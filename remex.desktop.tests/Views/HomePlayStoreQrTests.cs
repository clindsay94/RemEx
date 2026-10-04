using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// The Home screen's "Get the phone app" card opens a scan-to-install QR code instead of a browser on
/// the PC (RemEx-2p7um).
/// </summary>
/// <remarks>
/// The XAML half is a SOURCE-TEXT test for the reason <c>HomeViewCharacterisationTests</c> gives:
/// there is no headless render here, and Avalonia binding failures are silent.
/// </remarks>
public class HomePlayStoreQrTests
{
    private const string Listing = "https://play.google.com/store/apps/details?id=com.clindsay94.remex";

    [Fact]
    public void TheQrCodeEncodesTheListingWithTheInstallReferrer()
    {
        HomeViewModel.PlayStoreQrUrl.Should().Be(
            Listing + "&referrer=utm_source%3Dremex_pc%26utm_medium%3Dqr",
            "the code is the Play listing plus a Play Console install-attribution referrer, so scans "
            + "from the PC show up as their own install source");
    }

    [Fact]
    public void OpenInBrowserStillUsesThePlainListing()
    {
        // The referrer is for the QR route only. UserLauncherTests pins what the command launches;
        // this pins the constant it launches from.
        HomeViewModel.PlayStoreUrl.Should().Be(Listing);
    }

    [Fact]
    public void TheQrHelperReturnsAPng()
    {
        var png = HomeViewModel.BuildQrPng(HomeViewModel.PlayStoreQrUrl);

        png.Length.Should().BeGreaterThan(8);
        png.Take(8).Should().Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            "Avalonia's Bitmap is built from these bytes, and only a PNG is what the flyout expects");
    }

    [Fact]
    public void ThePlayCardOpensAFlyoutAndHasNoCommandOfItsOwn()
    {
        var card = PlayCard();

        // ANTI-VACUITY: every assertion below is about this region. If the matcher stops finding the
        // card, "contains no Command" would pass on an empty string.
        card.Should().NotBeEmpty("HomeView should still have the Google Play card");

        var openingTag = Regex.Match(card, @"^<Button\s[^>]*>", RegexOptions.Singleline).Value;
        openingTag.Should().NotBeEmpty();
        openingTag.Should().NotMatchRegex(@"\bCommand=",
            "a Button with both a Flyout and a Command opens the flyout and closes it again in the "
            + "same click (RemEx-acvny)");

        card.Should().Contain("<Button.Flyout>", "clicking the card shows the QR code in a flyout");
    }

    [Fact]
    public void TheFlyoutShowsTheQrCodeAndOffersTheBrowserRoute()
    {
        var flyout = Between(PlayCard(), "<Button.Flyout>", "</Button.Flyout>");
        flyout.Should().NotBeEmpty("the Play card's flyout should exist");

        flyout.Should().MatchRegex(@"<Image\b[^>]*Source=""\{Binding PlayStoreQrImage\}""",
            "the flyout's whole point is the scan-to-install code");
        typeof(HomeViewModel).GetProperty("PlayStoreQrImage", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull("the Image binds a property that has to exist on HomeViewModel");

        flyout.Should().MatchRegex(@"<Button\s[^>]*Command=""\{Binding OpenPlayStoreCommand\}""",
            "someone without a phone to hand still needs a way to open the listing");
    }

    // ─────────────────────────── plumbing ───────────────────────────

    /// <summary>
    /// The whole Play card Button element, from its opening tag to its matching close, or empty if
    /// the card can't be found.
    /// </summary>
    private static string PlayCard()
    {
        var home = Home();
        var start = Regex.Match(home, @"<Button\s[^>]*Home_PlayStoreCardDesc[^>]*>", RegexOptions.Singleline);
        if (!start.Success) return string.Empty;

        // Walk Button opens and closes from the card's own opening tag so the nested "Open in
        // browser instead" Button doesn't end the region early. Self-closing tags don't nest, and
        // the \s keeps <Button.Flyout> property elements out of the count.
        var depth = 0;
        foreach (Match tag in Regex.Matches(home[start.Index..], @"<Button\s[^>]*?(/?)>|</Button>", RegexOptions.Singleline))
        {
            if (tag.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else if (tag.Groups[1].Value != "/") depth++;

            if (depth == 0)
                return home.Substring(start.Index, tag.Index + tag.Length);
        }
        return string.Empty;
    }

    private static string Between(string text, string open, string close)
    {
        var from = text.IndexOf(open, StringComparison.Ordinal);
        var to = from < 0 ? -1 : text.IndexOf(close, from, StringComparison.Ordinal);
        return from >= 0 && to > from ? text[from..to] : string.Empty;
    }

    private static string Home()
        => File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "HomeView.axaml"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
