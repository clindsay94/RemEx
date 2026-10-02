using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Sweep D3 and D10: plain sentence-case labels on the PC, and sensor view names shared with the phone.
/// </summary>
public class PlainWordsAndViewNamesTests
{
    private static readonly string[] Locales =
        ["", ".es", ".fr", ".hi", ".id", ".pl", ".pt-BR", ".tr", ".uk"];

    /// <summary>
    /// The labels the cross-app cohesion spec (decision 2) moved off the terminal voice: no ALL CAPS
    /// baked into a translation, no decorative glyph prefix. Acronyms (CPU, MAC, PIN) are fine.
    /// </summary>
    public static TheoryData<string> PlainLabels =>
    [
        "Settings_Header", "Settings_General", "Settings_Help", "Settings_Connection",
        "Home_StatMemory", "Home_StatUptime", "Home_PhoneStatus", "Home_ClearActivity",
        "FileTransfer_QueueTitle", "FileTransfer_QueueKindUpload", "FileTransfer_QueueKindDownload",
        "FileTransfer_QueueKindSend", "FileTransfer_PropertiesTitle", "FileTransfer_VolumesTitle",
        "Custom_SectionFlyout", "Custom_SectionBehaviour", "Custom_SectionFonts", "SecondMetric_Header",
        "Remote_ThisPcMac", "About_Tagline", "Logs_Header", "RemoteDesktop_Header", "Remote_Header",
        "AppLauncher_Header", "Home_LogoTooltip", "Settings_PinnedSensors", "Canvas_ActiveSensors",
        "Canvas_NewSensors", "Tutorial_Header", "Settings_PairingPinTitle", "Settings_FileTransferSection",
        "Settings_BackupSection", "Settings_TrustSection", "Personalize_Subtitle",
        "Canvas_GraphType", "Canvas_GraphBar", "Canvas_GraphLine", "Canvas_GraphGauge", "Canvas_GraphRing",
        "Canvas_GraphLed", "Canvas_GraphGlow", "Canvas_GraphDual", "Canvas_GraphBigValue",
    ];

    [Theory]
    [MemberData(nameof(PlainLabels))]
    public void TheLabelIsPlainInEveryLocale(string key)
    {
        foreach (var locale in Locales)
        {
            var value = Value(locale, key);
            value.Should().NotBeNull($"{key} must exist in Strings{locale}.resx");

            // Words of four or more letters, with acronyms of up to four letters (CPU, MAC, UEFI)
            // allowed; anything longer in capitals is the shouting this replaced.
            var shouted = Regex.Matches(value!, @"\p{Lu}{5,}").Select(m => m.Value).ToArray();
            shouted.Should().BeEmpty($"Strings{locale}.resx {key} = \"{value}\" should be sentence case");

            char.IsLetterOrDigit(value![0]).Should().BeTrue(
                $"Strings{locale}.resx {key} = \"{value}\" starts with a decorative glyph");
            value.Should().NotMatchRegex(@"[▮╱╲━◍▫▒▓⑂⏻◈]", $"Strings{locale}.resx {key} carries a glyph");
        }
    }

    [Fact]
    public void TheTurkishUptimeLabelIsTurkish()
    {
        // It read "ÇALIŞMATivité" - half Turkish, half a French word.
        Value(".tr", "Home_StatUptime").Should().Be("Çalışma süresi");
    }

    /// <summary>
    /// The PC's sensor-view names are the phone's names (D10). English is checked against the phone's
    /// own strings.xml so the two cannot drift apart; Gauge and Glow have no phone counterpart.
    /// </summary>
    [Theory]
    [InlineData("Canvas_GraphType", "dashboard_view_picker_title")]
    [InlineData("Canvas_GraphSmart", "dashboard_view_auto")]
    [InlineData("Canvas_GraphBigValue", "dashboard_view_big_value")]
    [InlineData("Canvas_GraphBar", "dashboard_view_bar")]
    [InlineData("Canvas_GraphLine", "dashboard_view_line")]
    [InlineData("Canvas_GraphRing", "dashboard_view_ring_gauge")]
    [InlineData("Canvas_GraphLed", "dashboard_view_led_meter")]
    [InlineData("Canvas_GraphDual", "dashboard_view_dual_metric")]
    public void TheEnglishViewNameMatchesThePhone(string pcKey, string phoneKey)
    {
        var phone = File.ReadAllText(Path.Combine(RepoRoot(), "remex.android", "app", "src", "main", "res", "values", "strings.xml"));
        var match = Regex.Match(phone, $"<string name=\"{phoneKey}\"[^>]*>(.*?)</string>");
        match.Success.Should().BeTrue($"the phone defines {phoneKey}");

        Value("", pcKey).Should().Be(match.Groups[1].Value);
    }

    [Fact]
    public void BigValueIsOfferedAndDrawsNoGraph()
    {
        var canvas = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Views", "CanvasView.axaml"));
        canvas.Should().Contain("CommandParameter=\"BigValue\"", "the view picker offers Big value");

        var sensor = new SensorViewModel();
        sensor.IsBigValue.Should().BeFalse();
        sensor.SelectedGraphType = GraphType.BigValue;
        sensor.IsBigValue.Should().BeTrue("the card hides its graph and centres the value in this view");

        var card = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "SensorCardContent.axaml"));
        card.Should().Contain("IsVisible=\"{Binding !IsBigValue}\"", "the graph and the corner value hide in Big value");
        card.Should().Contain("IsVisible=\"{Binding IsBigValue}\"", "the centred value shows in Big value");
    }

    private static string? Value(string locale, string key)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Localization", $"Strings{locale}.resx"));
        var m = Regex.Match(text, $"<data name=\"{Regex.Escape(key)}\"[^>]*>\\s*<value>(.*?)</value>", RegexOptions.Singleline);
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remex.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (Remex.sln).");
    }
}
