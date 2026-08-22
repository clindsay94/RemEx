using Avalonia.Threading;
using FluentAssertions;
using Remex.Core.Messages;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Covers a production path that ends in <c>Dispatcher.UIThread.Post</c>, which nothing in this
/// assembly previously reached (RemEx-r8c6).
/// </summary>
/// <remarks>
/// <para>
/// THE BEAD'S DIAGNOSIS WAS WRONG, AND THAT IS THE USEFUL RESULT. It said the blocker was the
/// absence of an <c>Avalonia.Headless</c> reference — that nothing pumps the dispatcher, so a posted
/// delegate is queued and never runs. A headless harness was built, and then deleted again once
/// review measured what it actually bought: with no Avalonia application booted at all,
/// <c>Dispatcher.UIThread.CheckAccess()</c> is true even on a freshly spawned background thread, and
/// <c>Post</c> followed by <c>RunJobs()</c> runs the callback. The package reference, the
/// assembly-level <c>AvaloniaTestApplication</c> and the <c>[AvaloniaFact]</c> attribute made no
/// difference to whether these tests pass — measured by deleting all three and rebuilding clean.
/// </para>
/// <para>
/// So the gap was never a missing package. It was that no test called <c>RunJobs()</c>: the queue was
/// real, nothing drained it, and a posted callback that never runs looks exactly like an unchanged
/// collection with no error. That is the worst shape a gap can take — it does not read as missing
/// coverage, it reads as passing coverage.
/// </para>
/// <para>
/// The consequence is that the split between <c>ProcessTelemetry</c> and the internal
/// <c>ApplyTelemetry</c> — and the same shape in <c>SettingsViewModel</c>, where only a posted
/// callback subscribes <c>FileTrustDeviceItem.RevokeRequested</c> — was never blocked on
/// infrastructure. Any of those can be covered with three lines and no new dependency.
/// </para>
/// </remarks>
public class DispatcherPostedWorkTests
{
    private static TelemetryPayload OneSensor() => new()
    {
        Sensors = [new SensorReading { Name = "CPU Package", Value = 42, Unit = "°C", Category = "CPU" }],
    };

    [Fact]
    public void ProcessTelemetryRunsTheWorkItPosts()
    {
        // THE POST SITE, not the body. CanvasDashboardViewModel already had an internal ApplyTelemetry
        // so the LOGIC could be tested without a dispatcher; this covers the one line that schedules
        // it, which is the half where a failure is silent.
        //
        // layoutService and shell are stored and never dereferenced on this path, exactly as the
        // destructive-action suite already relies on.
        var vm = new CanvasDashboardViewModel(new ConnectionViewModel(), null!, null!);

        // THE QUEUE, HELD RATHER THAN RUN. Substituting the dispatch seam keeps the two things this
        // test has always cared about — that ProcessTelemetry SCHEDULES rather than executes, and
        // that what it schedules is the real ApplyTelemetry — while removing the dependency on
        // Avalonia's UI dispatcher that Avalonia 12 turned into a thread-affinity failure
        // (RemEx-jcma3). It is also strictly stronger than the old RunJobs() version: that one could
        // not tell "ProcessTelemetry posted our callback" from "ProcessTelemetry ran something else
        // that happened to stage a card", because it never held the callback in its hand.
        Action? posted = null;
        vm.Dispatch = work => posted = work;

        vm.ProcessTelemetry(OneSensor());

        posted.Should().NotBeNull("ProcessTelemetry must hand its work to the dispatcher, not run it inline");
        vm.StagedCards.Should().BeEmpty("scheduling is not executing — nothing has drained the queue yet");

        posted!();

        vm.StagedCards.Should().NotBeEmpty(
            "a sensor seen for the first time is staged by ApplyTelemetry, which only runs if the "
            + "posted callback actually executed");
    }

    [Fact]
    public void ProcessTelemetryDefaultsToTheRealUiDispatcher()
    {
        // ANTI-VACUITY FOR THE TEST ABOVE, and the reason the seam is not just a hole in the class.
        // Substituting Dispatch proves ProcessTelemetry routes through Dispatch; it says nothing
        // about where Dispatch goes when nobody substitutes it. If the default were left unset — or
        // quietly changed to run inline — the test above would still pass while the production path
        // no longer reached the UI thread at all, which is the silent-failure shape this whole file
        // was written about.
        //
        // Asserting the DEFAULT rather than calling it: invoking it would touch
        // Dispatcher.UIThread and bind it to this test's thread, which is precisely the accidental
        // binding that broke the suite in the first place.
        var vm = new CanvasDashboardViewModel(new ConnectionViewModel(), null!, null!);

        vm.Dispatch.Should().NotBeNull(
            "an unset dispatcher would make ProcessTelemetry throw on every real telemetry tick");

        // STARTSWITH, NOT EQUALS, and the difference is not pedantry. A `static` lambda is compiled
        // into a nested closure class, so the declaring type is "CanvasDashboardViewModel+<>c" and
        // an equality check fails against a perfectly correct default — which is how this assertion
        // was first written, and it failed on the very code it was meant to bless.
        vm.Dispatch.Method.DeclaringType!.FullName.Should().StartWith(
            typeof(CanvasDashboardViewModel).FullName,
            "the default has to be the class's own lambda, not something a previous test left behind");
    }
}
