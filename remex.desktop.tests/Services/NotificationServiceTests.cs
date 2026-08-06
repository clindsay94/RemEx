using FluentAssertions;
using Microsoft.Extensions.Logging;
using Remex.Core.Logging;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins what actually happens to a notification once the router has chosen a channel (RemEx-5wc2).
/// </summary>
/// <remarks>
/// <see cref="NotificationRouterTests"/> pins the DECISION. These pin the consequences of that
/// decision meeting a surface that may not be there — which is the part that decides whether an
/// event reaches a human or quietly does not.
/// </remarks>
public class NotificationServiceTests
{
    private sealed class RecordingToastSink : IInAppNotificationSink
    {
        public List<(NotificationImportance Importance, string Title, string Message)> Shown { get; } = new();

        public void Show(NotificationImportance importance, string title, string message) =>
            Shown.Add((importance, title, message));
    }

    private sealed class RecordingBalloonSink(bool succeeds) : ITrayBalloonSink
    {
        public List<(NotificationImportance Importance, string Title, string Message)> Shown { get; } = new();

        public bool TryShow(NotificationImportance importance, string title, string message)
        {
            Shown.Add((importance, title, message));
            return succeeds;
        }
    }

    private static NotificationService WithBothSurfaces(
        out RecordingToastSink toasts, out RecordingBalloonSink balloons, bool balloonSucceeds = true)
    {
        toasts = new RecordingToastSink();
        balloons = new RecordingBalloonSink(balloonSucceeds);
        return new NotificationService { InApp = toasts, TrayBalloon = balloons };
    }

    [Fact]
    public void AVisibleWindowGetsTheToastAndNoBalloon()
    {
        var service = WithBothSurfaces(out var toasts, out var balloons);

        var delivery = service.Present(
            NotificationImportance.Outcome, "File received", "report.pdf arrived.", windowVisible: true);

        Assert.Equal(NotificationDelivery.InApp, delivery);
        Assert.Equal((NotificationImportance.Outcome, "File received", "report.pdf arrived."), Assert.Single(toasts.Shown));
        Assert.Empty(balloons.Shown);
    }

    [Fact]
    public void AHiddenWindowGetsTheBalloonAndNoToast()
    {
        // The case that matters most: close-to-tray is on by default, so this is where most events
        // land, and it is the one that used to produce nothing visible at all.
        var service = WithBothSurfaces(out var toasts, out var balloons);

        var delivery = service.Present(
            NotificationImportance.Outcome, "File received", "report.pdf arrived.", windowVisible: false);

        Assert.Equal(NotificationDelivery.TrayBalloon, delivery);
        Assert.Equal((NotificationImportance.Outcome, "File received", "report.pdf arrived."), Assert.Single(balloons.Shown));
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public void InformationalChatterTouchesNoSurfaceWhileHidden()
    {
        var service = WithBothSurfaces(out var toasts, out var balloons);

        var delivery = service.Present(
            NotificationImportance.Informational, "Syncing", "42%", windowVisible: false);

        Assert.Equal(NotificationDelivery.Suppressed, delivery);
        Assert.Empty(toasts.Shown);
        Assert.Empty(balloons.Shown);
    }

    [Fact]
    public void ARefusedBalloonIsReportedAsFailedRatherThanDelivered()
    {
        // A sink that could not draw must not be indistinguishable from one that did. This is the
        // difference between "the user was told" and "we tried", and only the service can see it.
        var service = WithBothSurfaces(out _, out var balloons, balloonSucceeds: false);

        var delivery = service.Present(
            NotificationImportance.Problem, "Transfer failed", "The link dropped.", windowVisible: false);

        Assert.Equal(NotificationDelivery.Failed, delivery);
        Assert.Single(balloons.Shown);
    }

    [Fact]
    public void AMissingSurfaceIsFailedRatherThanACrash()
    {
        // Both surfaces are installed by the UI, and both errors this feature announces can fire
        // before that has happened - app initialization is one of them.
        var service = new NotificationService();

        Assert.Equal(NotificationDelivery.Failed,
            service.Present(NotificationImportance.Problem, "Startup", "…", windowVisible: true));
        Assert.Equal(NotificationDelivery.Failed,
            service.Present(NotificationImportance.Problem, "Startup", "…", windowVisible: false));
    }

    [Fact]
    public void AVisibleWindowWithNoToastHostFallsBackToTheBalloon()
    {
        // The toast host is installed by the shell, and app initialization failing is wired to this
        // - so the one notification most worth showing is the one most likely to be raised before
        // there is anywhere to show it. Ballooning is not the double notification the router guards
        // against, because the toast it would be doubling does not exist.
        var balloons = new RecordingBalloonSink(succeeds: true);
        var service = new NotificationService { TrayBalloon = balloons };

        var delivery = service.Present(
            NotificationImportance.Problem, "Startup", "…", windowVisible: true);

        Assert.Equal(NotificationDelivery.TrayBalloon, delivery);
        Assert.Single(balloons.Shown);
    }

    [Fact]
    public void AThrowingSurfaceIsFailedRatherThanAnEscapedException()
    {
        // Notify runs its body on the dispatcher, where an escaping exception has nowhere to be
        // observed - and the one thing it could not do there is announce itself.
        var service = new NotificationService { InApp = new ThrowingToastSink() };

        Assert.Equal(NotificationDelivery.Failed,
            service.Present(NotificationImportance.Outcome, "Export", "…", windowVisible: true));
    }

    [Fact]
    public void AFailedDeliveryLeavesARecord()
    {
        // The whole reason Failed is survivable rather than silent: suppressing the ANNOUNCEMENT is
        // a UI decision, suppressing the RECORD is the RemEx-43ha defect. The sink is not cleared
        // first - this test class shares that static with every other one in the suite.
        const string Title = "A notification with nowhere to go";
        var service = new NotificationService();

        service.Present(NotificationImportance.Problem, Title, "…", windowVisible: false);

        InMemoryLogSink.GetEntries()
            .Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains(Title));
    }

    private sealed class ThrowingToastSink : IInAppNotificationSink
    {
        public void Show(NotificationImportance importance, string title, string message) =>
            throw new InvalidOperationException("the toast host is not ready");
    }

    [Fact]
    public void AnUnanswerableVisibilityProbeIsTreatedAsHidden()
    {
        // Close-to-tray defaults on, so "do not know" is far more likely hidden - and the two
        // mistakes are not symmetric. Guessing visible announces to an empty screen; guessing
        // hidden merely double-notifies someone who could also have seen it in the window.
        var toasts = new RecordingToastSink();
        var balloons = new RecordingBalloonSink(succeeds: true);
        var service = new NotificationService
        {
            InApp = toasts,
            TrayBalloon = balloons,
            WindowVisibleProbe = null,
            Dispatch = work => work(),
        };

        service.Notify(NotificationImportance.Outcome, "File received", "report.pdf arrived.");

        Assert.Single(balloons.Shown);
        Assert.Empty(toasts.Shown);
    }

    [Fact]
    public void NotifyAsksTheProbeEveryTimeRatherThanCachingIt()
    {
        // The window is shown and hidden constantly; a probe read once at install time would route
        // every later event by the state the app happened to be in at startup.
        var toasts = new RecordingToastSink();
        var balloons = new RecordingBalloonSink(succeeds: true);
        var visible = true;
        var service = new NotificationService
        {
            InApp = toasts,
            TrayBalloon = balloons,
            WindowVisibleProbe = () => visible,
            Dispatch = work => work(),
        };

        service.Notify(NotificationImportance.Outcome, "First", "…");
        visible = false;
        service.Notify(NotificationImportance.Outcome, "Second", "…");

        Assert.Equal("First", Assert.Single(toasts.Shown).Title);
        Assert.Equal("Second", Assert.Single(balloons.Shown).Title);
    }
}
