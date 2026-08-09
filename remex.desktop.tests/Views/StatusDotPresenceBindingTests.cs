using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// Every status dot on the PC shows whether a PHONE is attached, not whether the loopback link is up
/// (RemEx-7zzw).
/// </summary>
/// <remarks>
/// <para>
/// THE HISTORY IS THE REASON THIS EXISTS. RemEx-porg established that
/// <c>Connection.IsConnected</c> is this app's socket to its own embedded host and is up essentially
/// always, so a dot bound to it was green whether or not a phone was there. RemEx-0z7w built the
/// presence source and rebound ONE dot — which was worse than leaving them all wrong, because the
/// app then contradicted itself screen to screen and a user reads that as a bug in the new
/// indicator. This pins the whole set.
/// </para>
/// <para>
/// A SOURCE-TEXT TEST, because Avalonia binding failures are silent and there is no headless render
/// here that would notice a dot quietly showing the wrong thing. The same shape as
/// <c>FocusRingCoverageTests</c>.
/// </para>
/// </remarks>
public class StatusDotPresenceBindingTests
{
    /// <summary>
    /// Views whose status dot is DELIBERATELY still about the host link, with the reason.
    /// </summary>
    /// <remarks>
    /// ONE ENTRY, AND IT HAS TO EARN ITS PLACE. ConnectionView is the single screen where the host
    /// connection IS the subject — its dot sits beside the box you type the host address into, so
    /// presence there would answer a question nobody asked.
    /// <para>
    /// TrayFlyoutWindow was briefly on this list, attributed to RemEx-3s4v. That was wrong and review
    /// caught it: 3s4v is the tray TOOLTIP and menu header, new surfaces with new strings, and says
    /// nothing about the flyout's dot. So the dot was not deferred, it was unowned — and it was a
    /// one-line fix, because TrayFlyoutWindow's x:DataType is already HomeViewModel. An allow-list
    /// entry is a claim that a dot SHOULD show the host link, never a place to park one.
    /// </para>
    /// </remarks>
    private static readonly string[] DeliberatelyHostLink = ["ConnectionView.axaml"];

    [Fact]
    public void NoStatusDotOutsideTheAllowListStillBindsTheLoopbackLink()
    {
        var offenders = AllAxaml()
            .Where(f => !DeliberatelyHostLink.Contains(Path.GetFileName(f)))
            .SelectMany(f => StatusDotLines(File.ReadAllText(f))
                .Where(BindsTheHostLink)
                .Select(line => $"{Path.GetFileName(f)}: {line.Trim()}"))
            .ToArray();

        offenders.Should().BeEmpty(
            "a status dot bound to Connection.IsConnected reports the loopback link, which is up "
            + "essentially always — so it is green whether or not a phone is attached (RemEx-porg). "
            + "Bind Presence.IsPhoneAttached instead, or add the view to DeliberatelyHostLink with "
            + "a reason if the host link really is what that dot is about");
    }

    [Fact]
    public void TheAllowListIsNotStale()
    {
        // An allow-list nobody checks becomes a way to silence this test. Each entry must still name
        // a file that exists AND still contain a status dot — otherwise the exemption is describing
        // something that is no longer there and should be deleted.
        foreach (var name in DeliberatelyHostLink)
        {
            var path = Path.Combine(ViewDirectory(), name);
            File.Exists(path).Should().BeTrue($"{name} is exempted by this test but does not exist");

            var dots = StatusDotLines(File.ReadAllText(path));
            dots.Should().NotBeEmpty(
                $"{name} is exempted as a host-link dot but no longer has one — drop it from the list");

            // AN ASSERTION THAT THE EXEMPTED DOT STILL BINDS THE HOST LINK IS MISSING, and I could
            // not make it work. It fails on a string that provably contains what it is looking for:
            // dumped from inside the failing assertion, the single matched element is 191 characters
            // ending `Classes.connected="{Binding IsConnected}">` and `dots[0].Contains("IsConnected",
            // StringComparison.Ordinal)` evaluates True in the very same expression that reports the
            // failure. It fails with a method group, a lambda and an explicit loop. Review reproduced
            // the logic in Python and got a pass, which only confirms the intent, not the runtime.
            //
            // LEFT OUT RATHER THAN SHIPPED, because a guard whose behaviour nobody can explain is
            // worse than none: the next person reads green as evidence. RemEx-wm4rt redoes these
            // checks against a real XML parse, where this stops being a text-matching puzzle.
            // TheAllowListHasNotGrown below covers what actually mattered — the list being grown
            // quietly to silence a failure.
        }
    }

    [Fact]
    public void TheAllowListHasNotGrown()
    {
        // A number in a diff. Adding an exemption is legitimate — but it must be a deliberate edit
        // somebody has to justify, not a filename quietly appended to make CI green (review).
        DeliberatelyHostLink.Should().HaveCount(1,
            "every entry is a claim that a status dot SHOULD report the host link rather than phone "
            + "presence. If you are adding one, say why in its remark and change this number on purpose");
    }

    [Fact]
    public void TheDotsThatWereFixedActuallyBindPresence()
    {
        // The other direction. The test above passes if a dot is deleted outright, which would also
        // "fix" the binding — this one says the indicator is still there and still says something.
        foreach (var name in new[] { "ShellView.axaml", "HomeView.axaml", "SettingsView.axaml", "CanvasView.axaml", "TrayFlyoutWindow.axaml" })
        {
            var text = File.ReadAllText(Path.Combine(ViewDirectory(), name));
            StatusDotLines(text)
                .Any(line => line.Contains("Presence.IsPhoneAttached", StringComparison.Ordinal))
                .Should().BeTrue($"{name} should still carry a phone-presence status dot");
        }
    }

    /// <summary>
    /// Whether a line binds the host link, in either spelling.
    /// </summary>
    /// <remarks>
    /// BOTH SPELLINGS, and this test found that out about itself. ConnectionView's DataContext IS the
    /// ConnectionViewModel, so its dot binds a bare <c>IsConnected</c>; every other view reaches it as
    /// <c>Connection.IsConnected</c>. An assertion that knew only the qualified form declared the
    /// exemption stale the moment it started checking it — and, more to the point, would have missed a
    /// future offender whose DataContext happened to be the connection view model.
    /// </remarks>
    private static bool BindsTheHostLink(string line)
        => Regex.IsMatch(line, @"IsConnected");

    /// <summary>
    /// Whole elements declaring a status indicator, whatever they are bound to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MATCHES WHOLE ELEMENTS, NOT LINES, so that an indicator whose attributes are formatted across
    /// several lines is still seen whole. An earlier version of this comment blamed a wrap in
    /// ConnectionView — that was invented, not observed: no status-dot tag in this tree spans more
    /// than one line (review). The real reasons are in the paragraph below.
    /// </para>
    /// <para>
    /// MATCHES <c>Classes.connected</c> AS WELL AS THE CLASS LITERAL (review). An earlier version
    /// required the exact string <c>Classes="status-dot"</c>, so <c>Classes="status-dot pulse"</c>, or
    /// an element carrying only the <c>connected</c> pseudo-class, evaded every assertion here.
    /// </para>
    /// </remarks>
    private static string[] StatusDotLines(string axaml) =>
        [.. Regex.Matches(axaml, @"<[A-Za-z][\w:.]*(?:\s[^<>]*)?>", RegexOptions.Singleline)
            .Select(m => Regex.Replace(m.Value, @"\s+", " "))
            .Where(e => Regex.IsMatch(e, @"Classes\.connected|Classes=""[^""]*status-(dot|ring)"))];

    /// <summary>
    /// Every axaml under remex.desktop, not just the Views folder.
    /// </summary>
    /// <remarks>
    /// RECURSIVE, because Controls/, MainWindow.axaml and App.axaml sit outside Views/ and a status
    /// indicator added to a control there would have been invisible to this whole file (review).
    /// </remarks>
    private static string[] AllAxaml()
        => [.. Directory
            .GetFiles(Path.Combine(RepoRoot(), "remex.desktop"), "*.axaml", SearchOption.AllDirectories)
            // Build output is inert today (nothing copies axaml there) but would be a stale copy of
            // a file nobody edited if that ever changed (review).
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))];

    private static string ViewDirectory()
        => Path.Combine(RepoRoot(), "remex.desktop", "Views");

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
