using System.Globalization;
using Remex.Agent.Services.ScreenCapture;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that the ffmpeg capture arguments survive a host whose language is not English (RemEx-clum).
///
/// THIS IS THE OTHER DIRECTION FROM <see cref="DisplayTopologyCultureTests"/>, AND IT BREAKS A
/// DIFFERENT SET OF LOCALES. That one covers reading numbers out of tool output, which 57 cultures
/// get wrong. This one covers writing a number into an argument, which 95 get wrong — every culture
/// whose <c>NegativeSign</c> is not the plain ASCII hyphen, including sv-SE, lt-LT and fi-FI with
/// U+2212 MINUS SIGN. ffmpeg parses none of them.
///
/// ONLY ONE VALUE IN THAT ARGUMENT STRING CAN ACTUALLY BE NEGATIVE, so only that one is asserted
/// here. <c>_screenLeft</c>/<c>_screenTop</c> are the virtual-desktop origin, the minimum over
/// outputs; the widths, heights and quality levels alongside them cannot be negative, and a positive
/// <c>int</c> has no culture-sensitive rendering to get wrong — a test for those could not fail, so
/// there is not one. They still go through the same helper, for the reason RemEx-hbma established:
/// one rule with no exceptions is what keeps the signed cases safe by construction.
///
/// Nothing between the tool output and this interpolation excludes a negative origin: both parse
/// patterns admit one on purpose — <c>XrandrGeometryRegex</c> matches <c>(?&lt;x&gt;[+-]\d+)</c> and
/// the kscreen one <c>(?&lt;x&gt;-?\d+)</c> — and RemEx-tiih's tests pin that. The x11grab path is
/// also not gated on X11: it is capture strategy 2 for every display server, so it inherits whatever
/// topology kscreen-doctor produced on a KDE session.
/// </summary>
public sealed class CaptureArgumentCultureTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;

    public CaptureArgumentCultureTests()
    {
        // Shaped like sv-SE: U+2212 MINUS SIGN rather than the ASCII hyphen. Constructed rather than
        // named, because which locales use which sign is ICU data that shifts between runtimes — a
        // test naming one would pin the .NET version instead of this code.
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "−";
        CultureInfo.CurrentCulture = hostile;
    }

    public void Dispose() => CultureInfo.CurrentCulture = _original;

    [Fact]
    public void TheHostileCultureManglesABareNegativeSoTheseTestsCannotPassVacuously()
    {
        // The control. Both assertions below are "this still renders as ASCII", which proves nothing
        // unless the ambient culture would in fact render it otherwise.
        Assert.NotEqual("-1920", (-1920).ToString());

        // And the reason the positive operands in the same argument string are not asserted anywhere:
        // there is no culture in which this fails, so a test for it could not fail either.
        Assert.Equal("1920", 1920.ToString());
    }

    [Fact]
    public void AMonitorLeftOfTheOriginKeepsTheAsciiSign()
    {
        // What breaks without the fix: ffmpeg is handed ":0+−1920,0" and refuses the input, so
        // capture never starts — not a wrong frame, no frame at all.
        Assert.Equal(":0+-1920,0", LinuxScreenCaptureService.BuildX11GrabInputArgument(":0", -1920, 0));
    }

    [Fact]
    public void AMonitorAboveTheOriginKeepsTheAsciiSign()
    {
        // The other axis, asserted separately because x and y are two distinct format calls and a fix
        // applied to only one of them would leave the test above green.
        Assert.Equal(":0.0+0,-1440", LinuxScreenCaptureService.BuildX11GrabInputArgument(":0.0", 0, -1440));
    }
}
