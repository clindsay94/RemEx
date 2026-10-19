using System.Text.RegularExpressions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Source pins for the file-sharing consent hint and the pairing QR's accessible name. A source scan,
/// like the other dialog tests, because there is no headless render to query a peer's name.
/// </summary>
public class ConsentAndQrCopySourceTests
{
    private static string ReadView(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "remex.desktop", "Views", fileName));

    [Fact]
    public void FileConsentContent_ExplainsWhereToTurnRememberingOff()
    {
        var xaml = ReadView("FileConsentContent.axaml");

        Assert.Contains("FileConsent_RememberHint", xaml);
        Assert.Contains("FileConsent_Remember}", xaml);
    }

    [Fact]
    public void PairingQrImage_CarriesAnAccessibleName()
    {
        var xaml = Regex.Replace(ReadView("PairingQrPanelView.axaml"), @"\s+", " ");

        var image = Regex.Match(xaml, @"<Image [^>]*QrCodeImage[^>]*>");
        Assert.True(image.Success, "anti-vacuity: the QR Image element must be found");
        Assert.Contains("AutomationProperties.Name=\"{conv:Localize A11y_PairingQrCode}\"", image.Value);
    }
}
