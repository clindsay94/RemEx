using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Remex.Core.Services.Command;
using Xunit;

namespace Remex.Core.Tests;

/// <summary>
/// Pins the programs and argument lists the power commands actually run (RemEx-msyn).
///
/// TWO THINGS ARE PINNED HERE AND ONLY ONE OF THEM IS THE BEAD. The bead was the last file in the
/// invariant-formatting series — RemEx-hbma, j7el, tiih, clum, wssm — and like the one before it
/// there was no defect: the Windows delay is clamped to <c>[0, 315360000]</c> and the Linux one is
/// <c>Math.Max(1, ...)</c>, so neither can be negative, and a positive integer has no
/// culture-sensitive rendering in .NET. Formatting it invariantly changes no emitted byte today.
///
/// THE FLAGS ARE THE PART THAT MATTERS, AND THEY NEED THE CALL SITE. <c>WindowsSystemCommandService</c>
/// carries a remark warning that switching <c>/r</c> to <c>/r /g</c> would arm Windows Automatic
/// Restart Sign-On, silently falsifying five confirmation strings that promise in nine languages that
/// RemEx cannot reach the PC until someone signs in — and that "no test would fail" (RemEx-mkq1).
/// A first version of these tests asserted the argument BUILDER, which could not have caught that:
/// the builder is handed the flags, so it can only echo back whatever the call site chose. These
/// drive the public methods through a launcher seam instead, and record what would have run.
/// </summary>
public class SystemCommandArgumentTests
{
    private sealed class Recorder
    {
        public List<(string FileName, string Arguments)> Calls { get; } = [];

        public void Launch(string fileName, string arguments) => Calls.Add((fileName, arguments));
    }

    // ---- Windows: what each command actually dispatches -----------------------------------------

    [Fact]
    public void TheWindowsPowerCommandsDispatchExactlyTheseProgramsAndArguments()
    {
        // Asserted as one table rather than eight tests, because the interesting property is the set:
        // every restart mode reaching shutdown.exe with the flags its confirmation string promises,
        // and no mode quietly acquiring an extra one.
        //
        // Lock and MonitorOff are absent because on Windows they are not processes at all — they go
        // straight to LockWorkStation and SendMessage, outside the launcher seam. Pinning those needs
        // a seam over the P/Invokes, which nothing has needed yet.
        var recorder = new Recorder();
        var service = new WindowsSystemCommandService(recorder.Launch);

        service.Shutdown(0);
        service.ForceShutdown(30);
        service.Restart(60);
        service.ForceRestart(0);
        service.RestartToUefi(5);
        service.Hibernate();
        service.SignOut();
        service.Sleep();

        Assert.Equal(
            [
                ("shutdown.exe", "/s /t 0"),
                ("shutdown.exe", "/s /f /t 30"),
                ("shutdown.exe", "/r /t 60"),
                ("shutdown.exe", "/r /f /t 0"),
                ("shutdown.exe", "/r /fw /t 5"),
                ("shutdown.exe", "/h"),
                ("shutdown.exe", "/l"),
                ("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0"),
            ],
            recorder.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANegativeWindowsDelayIsClampedToZeroRatherThanEmittedAsOne(int delaySeconds)
    {
        // The clamp is what makes the invariant formatting unobservable here, so it is asserted
        // rather than assumed: if NormalizeDelay ever stopped flooring at zero, a negative delay
        // would reach the argument list, and on the 95 cultures whose NegativeSign is not the ASCII
        // hyphen shutdown.exe would be handed something it cannot parse.
        var recorder = new Recorder();
        new WindowsSystemCommandService(recorder.Launch).Shutdown(delaySeconds);

        Assert.Equal(("shutdown.exe", "/s /t 0"), Assert.Single(recorder.Calls));
    }

    [Fact]
    public void AHugeWindowsDelayIsClampedToTheDocumentedCeiling()
    {
        // The other end of the same clamp. 315360000 seconds is ten years, shutdown.exe's maximum.
        var recorder = new Recorder();
        new WindowsSystemCommandService(recorder.Launch).Shutdown(int.MaxValue);

        Assert.Equal(("shutdown.exe", "/s /t 315360000"), Assert.Single(recorder.Calls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void NoWindowsRestartArmsAutomaticRestartSignOn(int delaySeconds)
    {
        // THE TEST THE /g REMARK ASKED FOR, and the reason it drives the service rather than the
        // builder. Confirm_Restart_Message, Confirm_ForceRestart_Message, Confirm_RebootUefi_Message,
        // Confirm_Shutdown_Message and Confirm_ForceShutdown_Message all tell the user, in nine
        // languages, that RemEx cannot reach the PC again until someone signs in on it — because the
        // agent starts from a per-user logon task that does not fire at the sign-in screen.
        // `shutdown /g` signs the user back in unattended, which would make all five false while
        // every other test stayed green.
        //
        // ERRING TOWARD A FALSE ALARM ON PURPOSE, after two wrong answers here. The first comment had
        // the reasoning backwards, claiming a substring check would let a regression through when it
        // is in fact the STRICTER of the two: Contains("/g") would also fire on a hypothetical
        // "/graceful", which is a false alarm, not a miss. The second replaced it with a token-PREFIX
        // check and claimed that covered "/r/g" — review injected exactly that and the guard passed.
        //
        // So this is the plain substring, which is the strictest of the three and the only one whose
        // description is true: it catches "/g", "/g:something" and "/r/g" alike. For a guard whose job
        // is protecting a promise made to users in nine languages, a false alarm someone has to come
        // and read is the safe direction to be wrong in.
        var recorder = new Recorder();
        var service = new WindowsSystemCommandService(recorder.Launch);

        service.Restart(delaySeconds);
        service.ForceRestart(delaySeconds);
        service.RestartToUefi(delaySeconds);

        Assert.All(recorder.Calls, call =>
            Assert.DoesNotContain("/g", call.Arguments, StringComparison.Ordinal));
    }

    // ---- Linux ----------------------------------------------------------------------------------

    [Fact]
    public void TheLinuxPowerCommandsDispatchExactlyTheseProgramsAndArguments()
    {
        // TWO OF THESE ARE HERE BECAUSE REVIEW COUNTED WHAT WAS ACTUALLY DRIVEN. An earlier version of
        // this file claimed to pin "every mode, both platforms" while leaving Linux ForceRestart,
        // RestartToUefi and MonitorOff untouched — so `systemctl reboot -i` and
        // `systemctl reboot --firmware-setup`, the two most destructive argument lists in the file,
        // were exactly the ones unpinned.
        //
        // ONE PATH IS STILL NOT PINNED, deliberately and with the reason stated rather than left to be
        // discovered: RestartToUefi with a POSITIVE delay does not dispatch at all. It starts a real
        // Task.Delay and reboots from the continuation, so asserting it would mean sleeping for the
        // delay. Only the immediate path is driven here.
        var recorder = new Recorder();
        var service = new LinuxSystemCommandService(recorder.Launch);

        service.Shutdown(0);
        service.Restart(600);
        service.ForceRestart(0);
        service.ForceRestart(120);
        service.RestartToUefi(0);
        service.Sleep();
        service.Hibernate();
        service.SignOut();
        service.Lock();
        service.MonitorOff();

        Assert.Equal(
            [
                ("shutdown", "-h now"),
                ("shutdown", "-r +10"),
                ("systemctl", "reboot -i"),
                ("shutdown", "-r +2"),
                ("systemctl", "reboot --firmware-setup"),
                ("systemctl", "suspend"),
                ("systemctl", "hibernate"),
                ("loginctl", "terminate-session self"),
                ("loginctl", "lock-session"),
                ("sh", "-c \"xset dpms force off\""),
            ],
            recorder.Calls);
    }

    [Fact]
    public void AnUndelayedLinuxForceShutdownUsesSystemctlWhileADelayedOneFallsBackToShutdown()
    {
        // Characterization of a real asymmetry rather than an endorsement of it: with no delay this
        // takes the forceful `systemctl poweroff -i` path, but with a delay it runs the SAME
        // non-forced `shutdown -h` the ordinary Shutdown does, so "force" stops meaning anything the
        // moment a delay is supplied. Pinned so that is a visible decision; whether it should differ
        // is not this bead's question.
        var recorder = new Recorder();
        var service = new LinuxSystemCommandService(recorder.Launch);

        service.ForceShutdown(0);
        service.ForceShutdown(120);
        service.Shutdown(120);

        Assert.Equal(
            [
                ("systemctl", "poweroff -i"),
                ("shutdown", "-h +2"),
                ("shutdown", "-h +2"),
            ],
            recorder.Calls);
    }

    [Theory]
    [InlineData(1, "-h +1")]
    [InlineData(30, "-h +1")]
    [InlineData(59, "-h +1")]
    [InlineData(61, "-h +2")]
    public void ASubMinuteLinuxDelayRoundsUpRatherThanDownToAnImmediateShutdown(int delaySeconds, string expected)
    {
        // `shutdown` takes minutes. A truncating conversion would turn "shut down in 30 seconds" into
        // "shut down now" — the difference between a warning and a surprise, with no way for the
        // caller to tell it happened.
        var recorder = new Recorder();
        new LinuxSystemCommandService(recorder.Launch).Shutdown(delaySeconds);

        Assert.Equal(("shutdown", expected), Assert.Single(recorder.Calls));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ANonPositiveLinuxDelayScheduleForNowRatherThanForAMinute(int delaySeconds)
    {
        var recorder = new Recorder();
        new LinuxSystemCommandService(recorder.Launch).Shutdown(delaySeconds);

        Assert.Equal(("shutdown", "-h now"), Assert.Single(recorder.Calls));
    }

    // ---- the rule the dispatch tables above cannot see -------------------------------------------

    [Theory]
    [InlineData("remex.core/Services/Command/WindowsSystemCommandService.cs",
        "internal static string BuildShutdownArguments", "private static int NormalizeDelay", "modeFlags", 120)]
    [InlineData("remex.core/Services/Command/LinuxSystemCommandService.cs",
        "internal static string BuildShutdownArgs", "private void ExecuteProcess", "modeArg", 250)]
    public void TheArgumentBuildersFormatEveryNumberThroughTheInvariantHelper(
        string relativePath, string startMarker, string endMarker, string stringOperand, int minBodyLength)
    {
        // MEASURED, NOT ASSUMED: reverting Arg() to a raw interpolation leaves every assertion above
        // green. Both delays are clamped non-negative, and a positive integer has no
        // culture-sensitive rendering in .NET, so the emitted string is byte-identical either way.
        // Nothing observable changes when the rule is dropped, which is why the rule is checked here
        // instead. The same reasoning, and the same shape of test, as RemEx-wssm.
        //
        // Newlines normalized because File.ReadAllText does not translate them and this repo is
        // edited on Windows; .gitattributes is `* text=auto eol=lf`, so this is defensive.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), relativePath))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startMarker} was renamed or removed; this test needs updating with it.");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"The end marker for {startMarker} moved; this test needs updating with it.");

        // THE END MARKER MUST BE STRUCTURAL, NOT PUNCTUATION. A first version used ";" for the
        // Windows row, which bounded the expression body that happened to be there — and review showed
        // that refactoring the builder to a block body with a local silently truncated the scan at the
        // first statement, capturing zero holes and passing GREEN while the interpolation was raw. Both
        // rows now end on the next member's signature, which survives that refactor.
        //
        // The floor is a backstop against the region collapsing entirely, NOT against it being
        // subtly short — the start marker alone is 45 characters, so a truncated body would still
        // clear a small global floor, which is how the ";" version got as far as review. It is
        // therefore per-row and measured rather than guessed: the Windows builder's body is 153
        // characters and the Linux one's is 300, so the floors sit at 120 and 250. A single shared
        // number cannot work here, and the first attempt at one was set from the Linux body and broke
        // the Windows row immediately.
        var body = Regex.Replace(source[start..end], "//[^\n]*", string.Empty);
        Assert.True(body.Length > minBodyLength,
            $"Only {body.Length} chars of {startMarker} survived (floor {minBodyLength}); the scan "
            + "below would see almost nothing.");

        // An allow-list: anything that is not Arg(...) and not the one string operand is an offence,
        // so a NEWLY added number fails here rather than only a revert of the existing ones.
        var offenders = Regex.Matches(body, @"\{([^{}]+)\}")
            .Select(m => m.Groups[1].Value)
            .Where(hole => !hole.TrimStart().StartsWith("Arg(", StringComparison.Ordinal))
            .Where(hole => !string.Equals(hole.Trim(), stringOperand, StringComparison.Ordinal))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These argument holes in {relativePath} are interpolated directly rather than through "
            + $"Arg(): {string.Join(", ", offenders.Select(o => $"{{{o}}}"))}. If they are numbers, "
            + "wrap them in Arg() — a positive value renders the same either way, so nothing will "
            + "look broken, but a signed one breaks on 95 locales.");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
