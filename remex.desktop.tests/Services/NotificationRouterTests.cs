using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins which channel an event reaches the user through (RemEx-1fxt).
/// </summary>
/// <remarks>
/// Close-to-tray defaults ON, so MOST EVENTS HAPPEN WHILE THE WINDOW IS HIDDEN. That inverts the
/// usual assumption: an in-app toast is not a sensible default that occasionally goes unseen, it is
/// a channel that is invisible most of the time.
/// </remarks>
public class NotificationRouterTests
{
    [Fact]
    public void AProblemReachesTheUserWhereverTheyAre()
    {
        // The one importance that may interrupt. The alternative is the state this bead exists to
        // fix: an error that happened while minimized and was never seen by anyone.
        Assert.Equal(NotificationChannel.InApp,
            NotificationRouter.Route(NotificationImportance.Problem, windowVisible: true));
        Assert.Equal(NotificationChannel.TrayBalloon,
            NotificationRouter.Route(NotificationImportance.Problem, windowVisible: false));
    }

    [Fact]
    public void AnOutcomeFollowsTheUserToTheTray()
    {
        // An outcome is something they ASKED for and are waiting on - a file they accepted, an
        // export they started. A received file currently produces zero visible feedback, and
        // routing it to a toast alone would leave that true whenever the window is hidden.
        Assert.Equal(NotificationChannel.InApp,
            NotificationRouter.Route(NotificationImportance.Outcome, windowVisible: true));
        Assert.Equal(NotificationChannel.TrayBalloon,
            NotificationRouter.Route(NotificationImportance.Outcome, windowVisible: false));
    }

    [Fact]
    public void InformationalChatterNeverBalloons()
    {
        // THE RULE THAT KEEPS THE FEATURE TOLERABLE, and it protects the Problem case rather than
        // itself. A balloon on every progress tick trains the user to dismiss balloons without
        // reading them - at which point errors arrive in a channel they have learned to ignore.
        Assert.Equal(NotificationChannel.InApp,
            NotificationRouter.Route(NotificationImportance.Informational, windowVisible: true));
        Assert.Equal(NotificationChannel.Suppressed,
            NotificationRouter.Route(NotificationImportance.Informational, windowVisible: false));
    }

    [Fact]
    public void NothingIsEverRoutedToAChannelTheUserCannotSee()
    {
        // The invariant behind all three rules: an in-app toast is only ever chosen when the window
        // is actually visible. A single wrong branch here reintroduces the whole defect silently,
        // because the code path still "shows a notification" - into a hidden window.
        foreach (var importance in Enum.GetValues<NotificationImportance>())
        {
            var hidden = NotificationRouter.Route(importance, windowVisible: false);

            Assert.NotEqual(NotificationChannel.InApp, hidden);
        }
    }

    [Fact]
    public void AVisibleWindowNeverGetsABalloon()
    {
        // The counterpart. A balloon for something already on screen is a double notification, and
        // it is worth stating that a window BEHIND other windows still counts as visible: the user
        // chose to leave it open.
        foreach (var importance in Enum.GetValues<NotificationImportance>())
        {
            var visible = NotificationRouter.Route(importance, windowVisible: true);

            Assert.NotEqual(NotificationChannel.TrayBalloon, visible);
        }
    }

    [Fact]
    public void ASuppressedAnnouncementIsStillDecidedSeparatelyFromWhetherItIsRecorded()
    {
        // Suppressing the ANNOUNCEMENT is a UI decision. Suppressing the RECORD would repeat the
        // swallowed-errors defect fixed in RemEx-43ha: the diagnostics export could not explain
        // what happened while the window was hidden, which is when most things happen.
        Assert.True(NotificationRouter.ShouldLog(NotificationImportance.Problem));
        Assert.True(NotificationRouter.ShouldLog(NotificationImportance.Outcome));
        Assert.False(NotificationRouter.ShouldLog(NotificationImportance.Informational));
    }

    [Fact]
    public void EveryImportanceHasARoutingDecision()
    {
        // A future importance added to the enum must not fall through to a default that silently
        // drops it - which, for a notification layer, is indistinguishable from the bug being fixed.
        foreach (var importance in Enum.GetValues<NotificationImportance>())
        {
            foreach (var visible in new[] { true, false })
            {
                var channel = NotificationRouter.Route(importance, visible);
                Assert.True(Enum.IsDefined(channel), $"{importance}/{visible} produced {channel}");
            }
        }
    }
}
