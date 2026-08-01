using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Remex.Agent.Services.Input;
using Remex.Agent.Services.Input.Linux;
using Remex.Core.Models;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins that numbers reaching a shell tool are formatted with the invariant culture (RemEx-hbma).
///
/// <c>NumberFormatInfo.NegativeSign</c> IS NOT ALWAYS AN ASCII HYPHEN. Several locales define it as
/// U+2212 MINUS SIGN, and a bare <c>int.ToString()</c> honours that. The result is an argument
/// neither xdotool nor ydotool can parse, on a host that works perfectly for anyone whose machine
/// happens to be set to a different language — the worst possible distribution of a bug, because the
/// person who can reproduce it is never the person who wrote it.
///
/// THE VALUE THAT CAN ACTUALLY GO NEGATIVE IS THE ONE OFF THE WIRE. <c>InputEvent.KeyCode</c> is an
/// unvalidated <c>int?</c>, so a negative reaches the ydotool <c>key</c> argument and the xdotool
/// key-name fallback without anything having to go wrong first. Relative pointer deltas are signed
/// by definition.
///
/// The tests force a hostile culture rather than naming a real locale, because which locales use
/// U+2212 is ICU data that changes between runtimes — a test that depended on it would be pinning
/// the .NET version rather than this code.
///
/// ONLY THE NEGATIVE CASES CAN FAIL, and that is a fact about .NET rather than a gap here. A positive
/// int formats identically in every culture, because int.ToString() applies neither NativeDigits nor
/// DigitSubstitution; NegativeSign is the only culture-dependent part of an integer. Measured, not
/// assumed: forcing the shared helper to CurrentCulture fails five tests in this file, and every one
/// of the five carries a negative. The positive cases below are characterizations of the argv, and
/// say so individually rather than being quietly presented as culture coverage.
/// </summary>
public sealed class CultureInvariantArgvTests : IDisposable
{
    private readonly CultureInfo _original = CultureInfo.CurrentCulture;

    public CultureInvariantArgvTests()
    {
        // A culture that formats negatives the way the bug requires, built rather than looked up.
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "−";
        CultureInfo.CurrentCulture = hostile;
    }

    public void Dispose() => CultureInfo.CurrentCulture = _original;

    private sealed class Recorder
    {
        public List<string[]> Calls { get; } = [];

        public InputToolLauncher Launcher => (_, _, arguments) =>
        {
            Calls.Add(arguments);
            return string.Empty;
        };

        public string[] Single() => Assert.Single(Calls);
    }

    private static LinuxDesktopBackendStatus Backend(LinuxDesktopTool tool) => new(
        DesktopEnvironment: "test",
        IsWaylandSession: false,
        IsKdePlasma: false,
        HasDisplayServer: true,
        InputTool: tool,
        InputToolPath: "/usr/bin/tool",
        CursorQueryTool: LinuxDesktopTool.None,
        CursorQueryToolPath: null,
        WindowControlTool: LinuxDesktopTool.None,
        WindowControlToolPath: null);

    private static (LinuxInputSimulationService Service, Recorder Recorder) New(LinuxDesktopTool tool)
    {
        var recorder = new Recorder();
        var service = new LinuxInputSimulationService(
            NullLogger<LinuxInputSimulationService>.Instance,
            Backend(tool),
            recorder.Launcher);
        return (service, recorder);
    }

    [Fact]
    public void TheHostileCultureIsRealSoTheseTestsCannotPassVacuously()
    {
        // The control. Every assertion below is "the output does NOT look like this", which is worth
        // nothing unless the ambient culture would in fact produce it. If a future runtime ignored
        // the override, the rest of this file would pass while testing nothing at all.
        Assert.Equal("−5", (-5).ToString(CultureInfo.CurrentCulture));
        Assert.Equal("-5", (-5).ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ARelativeMoveKeepsAnAsciiHyphen()
    {
        var (service, recorder) = New(LinuxDesktopTool.Xdotool);

        service.MouseMoveRelative(-5, -7);

        Assert.Equal(["mousemove_relative", "--", "-5", "-7"], recorder.Single());
    }

    [Fact]
    public void AnAbsoluteMoveOntoANegativeCoordinateDoesToo()
    {
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.MoveMouse(-1920, -50);

        Assert.Equal(["mousemove", "--absolute", "-x", "-1920", "-y", "-50"], recorder.Single());
    }

    [Fact]
    public void ANegativeKeyCodeOffTheWireKeepsAnAsciiHyphen()
    {
        // THE SITE THIS BEAD WAS REALLY ABOUT, and it is an interpolation rather than a ToString(),
        // which is why it survived a sweep for the latter. InputEvent.KeyCode is an unvalidated
        // int? straight off the network, so a negative arrives without anything having to go wrong
        // first. ProtocolKeyCodeToLinuxKeycode returns -1 for it, the ternary therefore falls back
        // to the protocol code itself, and the ydotool argument becomes "<negative>:1".
        //
        // A first draft of this test asserted "-1:1" for an ordinary UNMAPPED key and failed: the
        // fallback passes the protocol code, not the -1. Worth recording, because the wrong version
        // would have looked like coverage of the negative case while only ever exercising a positive
        // one.
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.KeyDown(-5);

        Assert.Equal(["key", "-5:1"], recorder.Single());
    }

    [Fact]
    public void TheKeyReleaseArgumentIsFormattedTheSameWay()
    {
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.KeyUp(-5);

        Assert.Equal(["key", "-5:0"], recorder.Single());
    }

    [Fact]
    public void AnUnmappedButPositiveKeyCodeFallsBackToTheProtocolCode()
    {
        // The behaviour the failed draft assumed was negative. Pinned as its own case so the
        // distinction between "unmapped" and "negative" stays visible: only the second exercises the
        // formatting rule, and conflating them is how a test comes to prove nothing.
        const int unmapped = 0xFFFE;
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.KeyDown(unmapped);

        Assert.Equal(["key", "65534:1"], recorder.Single());
    }

    [Fact]
    public void TheXdotoolKeyNameFallbackIsAlsoANumber()
    {
        // The other half of the same unmapped key: xdotool gets no XKB name, so the raw protocol
        // code is passed through. Positive here, but it is the same formatting decision and the
        // point of RemEx-hbma is that one rule with no exceptions is what makes the negative cases
        // safe by construction rather than by anyone remembering which values can go negative.
        const int unmapped = 0xFFFE;
        var (service, recorder) = New(LinuxDesktopTool.Xdotool);

        service.KeyDown(unmapped);

        Assert.Equal(["keydown", "65534"], recorder.Single());
    }

    [Fact]
    public void ButtonNumbersAreFormattedByTheSameRuleEvenThoughTheyCannotGoNegative()
    {
        // CANNOT FAIL ON CULTURE, AND SAYING SO IS THE POINT. A positive int formats identically in
        // every culture: .NET's int.ToString() applies neither NativeDigits nor DigitSubstitution,
        // so NegativeSign is the only culture-dependent part of an integer. Proven by injection
        // rather than assumed - forcing the shared helper to CurrentCulture fails five tests in this
        // file and leaves this one green.
        //
        // It is kept as a characterization of the argv, not as culture coverage. The reason the
        // production code routes positives through the same helper is that "one rule, no exceptions"
        // is what makes the negative cases safe by construction; a reader who has to work out which
        // values can go negative is one keystroke from getting it wrong.
        var (service, recorder) = New(LinuxDesktopTool.Xdotool);

        service.MouseDown(MouseButtons.Right);

        Assert.Equal(["mousedown", "3"], recorder.Single());
    }

    [Fact]
    public void ScrollDetentsSurviveTheHostileCultureToo()
    {
        var (service, recorder) = New(LinuxDesktopTool.Ydotool);

        service.MouseScroll(0, -240);

        Assert.Equal(["mousemove", "--wheel", "-x", "0", "-y", "-2"], recorder.Single());
    }

    [Fact]
    public void NoArgumentInTheInputTreeIsBuiltWithABareToString()
    {
        // A SOURCE-READING ASSERTION, and the honest reason is that the alternative here is no
        // coverage at all. The window-control service builds xdotool arguments from Width, Height
        // and DesktopNumber - unvalidated int? straight off the socket - and starts a process to do
        // it, with no launcher seam like the two input classes have. Flipping ITS helper to
        // CurrentCulture fails nothing behavioural, which was measured, so without this the newest
        // site of the bug would ship untested.
        //
        // Phrased as "no bare ToString anywhere in the file" rather than "the four known call sites
        // use Arg", because the failure this bead is about is a NEW formatting site added later by
        // someone who did not know the rule. Naming the current sites would pass forever while the
        // fifth one goes in beside them.
        //
        // IT CATCHES ONE OF THE TWO SHAPES, AND NOT THE ONE THAT HISTORICALLY BIT. Adding a fresh
        // `$"{limit}"` instead of a fresh `limit.ToString()` slips past this untouched - measured,
        // not supposed - and an interpolation is exactly how the `key:1` argument evaded the earlier
        // sweep. A blanket interpolation ban is not available: both scanned files legitimately
        // interpolate into developer-facing strings ("Unsupported desktop window action '{...}'",
        // "{toolPath} exited with code {...}"), so the assertion would fail on the first log message.
        // Stated rather than engineered around, because a guard whose reach is overstated is how the
        // next person concludes the class is covered.
        foreach (var name in new[] { "LinuxDesktopWindowControlService", "LinuxInputSimulationService" })
        {
            var path = Path.Combine("..", "..", "..", "..", "remex.agent", "Services", "Input", name + ".cs");
            Assert.True(File.Exists(path), $"expected to find {name} at {Path.GetFullPath(path)}");

            var code = Regex.Replace(File.ReadAllText(path), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            code = Regex.Replace(code, @"//.*", string.Empty);

            Assert.DoesNotContain(".ToString()", code);
        }
    }

    [Fact]
    public void TheWindowControlHelperIsInvariantToo()
    {
        // Direct cover for the helper the source scan above protects the call sites of. Between the
        // two, a regression has to defeat both a "does this file format numbers the wrong way" check
        // and a "does the helper do the right thing" check.
        Assert.Equal("-5", LinuxDesktopWindowControlService.Arg(-5));
        Assert.Equal("1920", LinuxDesktopWindowControlService.Arg(1920));
    }

    [Fact]
    public void TheFunctionKeyNameIsAFormattedNumberButCannotVaryByCultureEither()
    {
        // F1..F12 come from interpolating `keyCode - 0x6F`, a number formatted into a key NAME
        // rather than into an argument - a third shape of the same decision. The match arm is range
        // guarded to 0x70..0x7B, so the result is always 1..12 and, by the rule above, invariant
        // whatever the culture.
        //
        // SO THE PRODUCTION CHANGE THERE IS PROVABLY A NO-OP, and this test cannot fail on culture:
        // reverting that one site to a plain interpolation leaves all twelve tests green, which was
        // measured, not assumed. It is still worth making, because the alternative is a file where
        // some numeric formatting is explicit and some is not and nothing distinguishes deliberate
        // from overlooked. What is not worth doing is claiming coverage for it.
        Assert.Equal("F1", LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(0x70));
        Assert.Equal("F12", LinuxInputEventTranslator.ProtocolKeyCodeToXkbName(0x7B));
    }
}
