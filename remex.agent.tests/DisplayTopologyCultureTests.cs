using System.Globalization;
using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that display topology survives a host whose language is not English (RemEx-tiih).
///
/// THE TOOLS SPEAK ASCII AND THE HOST MIGHT NOT. <c>kscreen-doctor</c>, <c>xrandr</c> and
/// <c>xdpyinfo</c> emit plain ASCII signs; <c>int.Parse</c> and <c>int.TryParse</c> without a
/// provider use <c>CurrentCulture</c>; and 57 runtime cultures — the ar, ckb, fa, he, ks, lrc, mzn,
/// pa, ps, sd, ur and uz families — reject that sign, because theirs carries a directional mark such
/// as U+061C or U+200E in front of it.
///
/// THE BLAST RADIUS IS WIDER THAN THE BEAD SAID, which is why the interesting test here drives the
/// regex and the parse together rather than the helper alone. <c>XrandrGeometryRegex</c> matches
/// <c>(?&lt;x&gt;[+-]\d+)</c> — a sign is REQUIRED — so even a primary monitor sitting at the origin
/// arrives as <c>+0</c>. Measured: <c>"+0"</c>, <c>"+1920"</c> and <c>"-1920"</c> are rejected by the
/// same 57 cultures, and an unsigned <c>"1920"</c> by none. So on an affected host the xrandr path
/// failed for EVERY output, not only for a monitor left of or above the primary.
///
/// What that costs is not a cosmetic topology error. The virtual-desktop origin is the minimum over
/// outputs, and RemEx-dyvd made it load-bearing: <c>MoveMouse</c> subtracts it before handing
/// coordinates to ydotool, whose <c>--absolute</c> is emulated as home-then-move and wants an offset
/// rather than a position. A wrong origin aims the pointer somewhere else entirely, silently.
/// </summary>
public sealed class DisplayTopologyCultureTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;

    public DisplayTopologyCultureTests()
    {
        // Shaped like `ar`: ARABIC LETTER MARK then the ASCII sign, which is what actually rejects
        // the bare sign these tools emit. Constructed rather than named, because which locales use
        // which mark is ICU data that shifts between runtimes — a test naming one would pin the .NET
        // version instead of this code.
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "؜-";
        hostile.NumberFormat.PositiveSign = "؜+";
        CultureInfo.CurrentCulture = hostile;
    }

    public void Dispose() => CultureInfo.CurrentCulture = _original;

    [Fact]
    public void TheHostileCultureRejectsBothSignsSoTheseTestsCannotPassVacuously()
    {
        // The control. Every assertion below is "this still parses", which proves nothing unless the
        // ambient culture would in fact reject it. Note BOTH signs: the positive one matters here in
        // a way it did not for RemEx-j7el, because xrandr writes "+0" for an output at the origin.
        Assert.False(int.TryParse("-1920", NumberStyles.Integer, CultureInfo.CurrentCulture, out _));
        Assert.False(int.TryParse("+0", NumberStyles.Integer, CultureInfo.CurrentCulture, out _));
        Assert.True(int.TryParse("1920", NumberStyles.Integer, CultureInfo.CurrentCulture, out _));
    }

    [Fact]
    public void AnOrdinaryPrimaryMonitorAtTheOriginStillParses()
    {
        // THE CASE THE BEAD MISSED. "+0" is what xrandr writes for an output at the origin, so this
        // is not an edge case at all — it is every single-monitor host on an affected locale.
        Assert.True(LinuxScreenCaptureService.TryParseXrandrGeometry(
            "HDMI-1 connected primary 1920x1080+0+0 (normal left inverted right x axis y axis) 598mm x 336mm",
            out var width, out var height, out var x, out var y));

        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void AMonitorLeftOfPrimaryStillParses()
    {
        // The case the bead DID identify, and the one that corrupts the origin rather than merely
        // losing an output: the virtual-desktop left edge is the minimum over outputs, so dropping
        // this one silently moves the origin to 0 and takes the ydotool translation with it.
        Assert.True(LinuxScreenCaptureService.TryParseXrandrGeometry(
            "DP-2 connected 1920x1080-1920+0 (normal left inverted right x axis y axis) 598mm x 336mm",
            out var width, out var height, out var x, out var y));

        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
        Assert.Equal(-1920, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void AMonitorAboveThePrimaryStillParses()
    {
        // The other axis. Asserted separately because x and y are parsed by two different calls, and
        // a fix applied to one of them would leave this green if the two were tested together.
        Assert.True(LinuxScreenCaptureService.TryParseXrandrGeometry(
            "DP-3 connected 2560x1440+0-1440 (normal left inverted right x axis y axis) 700mm x 390mm",
            out _, out _, out var x, out var y));

        Assert.Equal(0, x);
        Assert.Equal(-1440, y);
    }

    [Fact]
    public void TheKdePathParsesANegativeGeometryToo()
    {
        // THE SECOND TOOL, AND IT WAS UNPINNED UNTIL THIS TEST. Reverting only the kscreen path's
        // helper failed nothing, because every other assertion here drives the xrandr regex. KDE is
        // the primary desktop this project targets, so leaving its parse uncovered would have been
        // the wrong half to skip.
        //
        // Note this path uses the THROWING int.Parse, inside a try/catch that treats any failure as
        // "no displays" - so on an affected host it did not merely mis-parse one line, it discarded
        // the entire kscreen result and fell through to xrandr, which was broken in the same way.
        const string output =
                "Output: 1 DP-1 abc-123\n" +
                "\tenabled\n" +
                "\tconnected\n" +
                "\tpriority 2\n" +
                "\tGeometry: -1920,0 1920x1080\n" +
                "Output: 2 HDMI-1 def-456\n" +
                "\tenabled\n" +
                "\tconnected\n" +
                "\tpriority 1\n" +
                "\tGeometry: 0,0 2560x1440\n";

        var displays = LinuxScreenCaptureService.ParseKScreenDisplays(output);

        Assert.Equal(2, displays.Count);

        var left = Assert.Single(displays, d => d.DisplayId == "DP-1" || d.Name == "DP-1");
        Assert.Equal(-1920, left.Left);
        Assert.Equal(0, left.Top);
        Assert.Equal(1920, left.Width);
        Assert.Equal(1080, left.Height);

        var primary = Assert.Single(displays, d => d.IsPrimary);
        Assert.Equal(0, primary.Left);
        Assert.Equal(2560, primary.Width);
    }

    [Fact]
    public void AConnectedLineWithAMalformedGeometryTokenIsStillRejected()
    {
        // Non-regression: becoming culture-invariant must not have made the parse permissive, or the
        // topology silently becomes a 0x0 output instead of falling through to the next detection
        // method.
        //
        // THE LINE HAS TO SAY "connected" FOR THIS TO MEAN ANYTHING, which a first version of this
        // test got wrong. It used a "disconnected" line, which is rejected by the guard at the top of
        // the method and never reaches the regex, the split or any parse helper - so it passed under
        // BOTH injections and pinned nothing. This one is connected with a truncated token (no +y),
        // so it genuinely exercises the regex-miss path.
        Assert.False(LinuxScreenCaptureService.TryParseXrandrGeometry(
            "DP-1 connected 1920x1080+0 (normal left inverted right x axis y axis) 598mm x 336mm",
            out _, out _, out _, out _));

        // And the guard itself, asserted deliberately rather than by accident.
        Assert.False(LinuxScreenCaptureService.TryParseXrandrGeometry(
            "HDMI-1 disconnected (normal left inverted right x axis y axis)",
            out _, out _, out _, out _));
    }
}
