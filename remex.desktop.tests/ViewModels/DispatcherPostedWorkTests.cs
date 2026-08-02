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

        vm.ProcessTelemetry(OneSensor());

        // Nothing has run yet: Post queues, it does not execute inline. Asserted so the RunJobs below
        // is visibly the thing that matters rather than decoration — and it is load-bearing, verified
        // by deleting that call, which reddens the assertion at the end.
        vm.StagedCards.Should().BeEmpty("Post only queues the callback — nothing has drained it yet");

        Dispatcher.UIThread.RunJobs();

        vm.StagedCards.Should().NotBeEmpty(
            "a sensor seen for the first time is staged by ApplyTelemetry, which only runs if the "
            + "posted callback actually executed");
    }
}
