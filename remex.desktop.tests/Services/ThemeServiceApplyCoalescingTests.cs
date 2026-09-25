using System.Collections.Generic;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// A Personalize slider drag calls <see cref="ThemeService.ApplyCustomization"/> on every tick —
/// dozens before the dispatcher runs even one. Each call used to post its own closure over the
/// settings it was given, so the queue backed up with full applies of settings already superseded
/// by the time they ran (perf audit P1-3). Fix: one post per dispatcher tick; a call while a post
/// is already queued only updates the pending value.
/// </summary>
public class ThemeServiceApplyCoalescingTests
{
    private static (ThemeService Theme, List<Action> Posted) NewTheme()
    {
        var posted = new List<Action>();
        var theme = new ThemeService { PostToUiThread = posted.Add };
        return (theme, posted);
    }

    [Fact]
    public void RapidCallsBeforeTheDispatcherRuns_QueueOnlyOnePost()
    {
        var (theme, posted) = NewTheme();

        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Dark" });
        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Light" });
        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Dark" });

        posted.Should().HaveCount(1, "three ticks before the dispatcher runs must collapse to one queued apply");
    }

    [Fact]
    public void TheQueuedPost_AppliesTheLatestSettings_NotTheFirst()
    {
        var (theme, posted) = NewTheme();
        CustomizationSettings? applied = null;
        theme.CustomizationApplied += s => applied = s;

        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Dark" });
        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Light" });

        posted.Should().HaveCount(1);
        posted[0]();

        applied.Should().NotBeNull();
        applied!.ThemeMode.Should().Be("Light", "the queued apply must read the settings fresh, not the ones captured when it was posted");
    }

    [Fact]
    public void AfterAPostRuns_TheNextCall_QueuesItsOwnPost()
    {
        var (theme, posted) = NewTheme();

        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Dark" });
        posted.Should().HaveCount(1);
        posted[0](); // resets the pending flag

        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Light" });

        posted.Should().HaveCount(2, "a call after the previous post already ran must queue a fresh one, not be dropped");
    }

    [Fact]
    public void ASettingsChange_DuringThePostItself_QueuesItsOwnFollowUpPost()
    {
        // The pending flag resets as the FIRST line of the posted callback, BEFORE it applies, so a
        // change made from inside CustomizationApplied (fired by the apply itself) must see the flag
        // already clear and queue its own post. If the reset instead ran AFTER the apply, this
        // reentrant call would find the flag still set and be silently absorbed - this test fails in
        // that ordering, unlike the reentrant-callback version it replaces.
        var posted = new List<Action>();
        ThemeService? theme = null;
        var reentered = false;
        theme = new ThemeService { PostToUiThread = posted.Add };
        theme.CustomizationApplied += _ =>
        {
            if (reentered) return;
            reentered = true;
            theme!.ApplyCustomization(new CustomizationSettings { ThemeMode = "Light" });
        };

        theme.ApplyCustomization(new CustomizationSettings { ThemeMode = "Dark" });
        posted.Should().HaveCount(1);

        posted[0](); // runs the real callback: resets the flag, then applies and fires CustomizationApplied

        posted.Should().HaveCount(2, "a change made from inside the apply itself must queue its own follow-up post");
    }
}
