using Remex.Agent.Services;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Pins the real exit-code mapping behind the launch-at-login read-back (RemEx-h5lr).
/// </summary>
/// <remarks>
/// **THIS FILE EXISTS BECAUSE MUTATION TESTING FOUND THE FIRST ATTEMPT UNPINNED.** The read-back was
/// covered only through a fake `IStartupRegistrationService`, so inverting the production mapping —
/// making a failed query report "not registered" — changed nothing any test could see. A test that
/// exercises a stand-in verifies the stand-in.
///
/// What the mapping decides is whether the settings switch can lie. It is also the write control, so
/// a false "off" invites the user to issue a real registration against a state nobody established.
/// </remarks>
public class SchtasksExitInterpretationTests
{
    [Fact]
    public void ZeroMeansTheTaskExists()
    {
        Assert.True(StartupRegistrationService.InterpretSchtasksExit(0));
    }

    [Fact]
    public void OneMeansTheTaskDoesNotExist()
    {
        // The only non-zero code schtasks documents as an ANSWER rather than a failure.
        Assert.False(StartupRegistrationService.InterpretSchtasksExit(1));
    }

    [Fact]
    public void TheLauncherNotRunningAtAllIsUnknown_NotNotRegistered()
    {
        // Null is "schtasks.exe did not run" - blocked by an EDR, missing from PATH, refused. That
        // says nothing about the task, and reporting it as "off" is how the switch comes to show a
        // state the machine is not in.
        Assert.Null(StartupRegistrationService.InterpretSchtasksExit(null));
    }

    [Fact]
    public void AnyOtherExitCodeIsUnknown()
    {
        // An unavailable Task Scheduler RPC endpoint, an access denial, a malformed name. None of
        // them is an answer about whether the task exists.
        foreach (var code in new[] { 2, 5, 267011, -1, int.MinValue, int.MaxValue })
        {
            Assert.Null(StartupRegistrationService.InterpretSchtasksExit(code));
        }
    }

    [Fact]
    public void UnknownNarrowsToNotRegistered_NeverToRegistered()
    {
        // THE DIRECTION MATTERS AND MUTATION TESTING IS WHAT PINNED IT. Narrowing unknown to true
        // would have the card and the switch both claim autostart is set up on a machine where
        // nothing established that - and the user finds out at the next reboot, which is the exact
        // failure this read-back exists to catch. Losing the distinction is fine; inverting it is not.
        Assert.False(StartupRegistrationService.NarrowToTwoValues(null));
        Assert.False(StartupRegistrationService.NarrowToTwoValues(false));
        Assert.True(StartupRegistrationService.NarrowToTwoValues(true));
    }

    [Fact]
    public void OnlyZeroEverReportsRegistered()
    {
        // Swept in the direction that matters most: nothing but a documented success may produce a
        // confident "yes", or the card and the switch would both claim autostart is set up when it
        // is not - and the user would find out at the next reboot.
        foreach (var code in new int?[] { null, 1, 2, 3, 5, -1, 267011 })
        {
            Assert.NotEqual(true, StartupRegistrationService.InterpretSchtasksExit(code));
        }
    }
}
