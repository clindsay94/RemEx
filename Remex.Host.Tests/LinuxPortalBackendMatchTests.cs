using System.IO;
using System.Runtime.Versioning;
using Remex.Host.Services.RemoteDesktop.Linux;
using Remex.Host.Services.RemoteDesktop.Linux.Portal;
using Xunit;

namespace Remex.Host.Tests;

/// <summary>
/// Unit tests for the .portal file parser used to distinguish
/// "backend not installed" from "backend installed but frontend stale".
/// Exercises <see cref="LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk(string, System.Collections.Generic.IReadOnlyList{string})"/>
/// against synthetic .portal files written to a temp directory.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPortalBackendMatchTests
{
    [Fact]
    public void Match_KdePortal_MatchesKdeDesktop()
    {
        using var tmp = new TempPortalDir();
        tmp.Write("kde.portal", """
            [portal]
            DBusName=org.freedesktop.impl.portal.desktop.kde
            Interfaces=org.freedesktop.impl.portal.ScreenCast;org.freedesktop.impl.portal.RemoteDesktop;
            UseIn=KDE
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("KDE", new[] { tmp.Path });

        Assert.True(match.AnyBackendInstalled);
        Assert.True(match.BackendImplementsRemoteDesktop);
        Assert.Equal("xdg-desktop-portal-kde", match.PackageName);
    }

    [Fact]
    public void Match_KdePortal_MatchesKdePlasmaMultiToken()
    {
        // XDG_CURRENT_DESKTOP=KDE:Plasma is what Plasma 6 sets.
        using var tmp = new TempPortalDir();
        tmp.Write("kde.portal", """
            [portal]
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop
            UseIn=KDE
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("KDE:Plasma", new[] { tmp.Path });

        Assert.True(match.BackendImplementsRemoteDesktop);
        Assert.Equal("xdg-desktop-portal-kde", match.PackageName);
    }

    [Fact]
    public void Match_GnomePortal_MatchesGnomeUnitySemicolonForm()
    {
        // Some GNOME-derived sessions set GNOME;Unity.
        using var tmp = new TempPortalDir();
        tmp.Write("gnome.portal", """
            [portal]
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop;org.freedesktop.impl.portal.ScreenCast
            UseIn=GNOME;Unity
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("Unity", new[] { tmp.Path });

        Assert.True(match.BackendImplementsRemoteDesktop);
        Assert.Equal("xdg-desktop-portal-gnome", match.PackageName);
    }

    [Fact]
    public void Match_KdePortal_DoesNotMatchGnomeDesktop()
    {
        using var tmp = new TempPortalDir();
        tmp.Write("kde.portal", """
            [portal]
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop
            UseIn=KDE
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("GNOME", new[] { tmp.Path });

        // KDE backend is on disk but cannot serve a GNOME session. The matcher
        // must NOT claim it implements RemoteDesktop for this desktop — that
        // would route the GNOME user to "restart the portal" instead of the
        // correct "install xdg-desktop-portal-gnome" advice. The package name
        // is still surfaced as a weak suggestion (best guess at what to install
        // would actually be -gnome, but the matcher only sees what's on disk).
        Assert.True(match.AnyBackendInstalled);
        Assert.False(match.BackendImplementsRemoteDesktop);
        Assert.Equal("xdg-desktop-portal-kde", match.PackageName);
    }

    [Fact]
    public void Match_NotInUseRespected()
    {
        using var tmp = new TempPortalDir();
        tmp.Write("generic.portal", """
            [portal]
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop
            NotInUse=KDE
            """);

        // Backend says "use me everywhere EXCEPT KDE". Current session IS KDE,
        // so NotInUse excludes it. Same gating as the GNOME case above:
        // BackendImplementsRemoteDesktop must be false so the caller doesn't
        // wrongly recommend restarting the portal.
        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("KDE", new[] { tmp.Path });

        Assert.True(match.AnyBackendInstalled);
        Assert.False(match.BackendImplementsRemoteDesktop);
    }

    [Fact]
    public void Match_NoMatchingBackend_BackendNotInstalled()
    {
        // Only gtk.portal is present — and gtk does NOT implement RemoteDesktop.
        using var tmp = new TempPortalDir();
        tmp.Write("gtk.portal", """
            [portal]
            Interfaces=org.freedesktop.impl.portal.FileChooser;org.freedesktop.impl.portal.Print
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("KDE", new[] { tmp.Path });

        Assert.True(match.AnyBackendInstalled);
        Assert.False(match.BackendImplementsRemoteDesktop);
        Assert.Null(match.PackageName);
    }

    [Fact]
    public void Match_EmptyDesktop_FindsFallbackCandidate()
    {
        // When XDG_CURRENT_DESKTOP is unset (the stale-systemd-user case), no
        // UseIn match succeeds. We still want to surface a candidate backend.
        using var tmp = new TempPortalDir();
        tmp.Write("kde.portal", """
            [portal]
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop
            UseIn=KDE
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("", new[] { tmp.Path });

        Assert.True(match.AnyBackendInstalled);
        Assert.True(match.BackendImplementsRemoteDesktop);
        Assert.Equal("xdg-desktop-portal-kde", match.PackageName);
    }

    [Fact]
    public void Match_BackendWithoutUseIn_AlwaysMatches()
    {
        // No UseIn/NotInUse → backend is desktop-agnostic.
        using var tmp = new TempPortalDir();
        tmp.Write("generic.portal", """
            [portal]
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("KDE", new[] { tmp.Path });

        Assert.True(match.BackendImplementsRemoteDesktop);
        Assert.Equal("xdg-desktop-portal-generic", match.PackageName);
    }

    [Fact]
    public void Match_MissingDirectory_ReturnsEmpty()
    {
        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk(
            "KDE", new[] { "/nonexistent/path/" + System.Guid.NewGuid() });

        Assert.False(match.AnyBackendInstalled);
        Assert.False(match.BackendImplementsRemoteDesktop);
        Assert.Null(match.PackageName);
    }

    [Fact]
    public void Match_IgnoresCommentsAndUnknownSections()
    {
        using var tmp = new TempPortalDir();
        tmp.Write("kde.portal", """
            # leading comment
            ; semicolon comment
            [other-section]
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop
            [portal]
            ; inline-section comment
            Interfaces=org.freedesktop.impl.portal.RemoteDesktop
            UseIn=KDE
            """);

        var match = LinuxRemoteDesktopPrerequisites.ProbePortalBackendsFromDisk("KDE", new[] { tmp.Path });
        Assert.True(match.BackendImplementsRemoteDesktop);
    }

    // ── PortalRecoveryHelper one-shot semantics ────────────────────────

    [Fact]
    public void RecoveryHelper_ShouldAttempt_ReturnsTrueOnceThenFalse()
    {
        PortalRecoveryHelper.ResetForTests();

        Assert.True(PortalRecoveryHelper.ShouldAttempt());
        Assert.False(PortalRecoveryHelper.ShouldAttempt());
        Assert.False(PortalRecoveryHelper.ShouldAttempt());

        // Reset and try again to confirm the test hook works.
        PortalRecoveryHelper.ResetForTests();
        Assert.True(PortalRecoveryHelper.ShouldAttempt());
    }

    // ── Test helpers ───────────────────────────────────────────────────

    private sealed class TempPortalDir : System.IDisposable
    {
        public string Path { get; }

        public TempPortalDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "remex-portal-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Write(string filename, string contents)
            => File.WriteAllText(System.IO.Path.Combine(Path, filename), contents);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
