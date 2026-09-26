using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Guards MicaBackdrop.cs's DWM interop (RemEx-rq0xl). Source-level, the same constraint as
/// WindowChromeBackdropTests: this suite has no headless Avalonia render, so a <c>Window</c>
/// constructed here has no platform handle to actually call <c>DwmSetWindowAttribute</c> against,
/// and the OS build number cannot be faked from a test. What CAN be pinned is the literal
/// attribute ids and values the spike (task-1-brief.md) proved work, and the two early-outs that
/// keep <c>TryApply</c> from ever throwing.
/// </summary>
public class MicaBackdropTests
{
    [Fact]
    public void IsSupported_GatesOn22H2()
    {
        // Build 22621 is Windows 11 22H2, the first build DWM honours
        // DWMWA_SYSTEMBACKDROP_TYPE (38) — the mica-spike found it reads back 0 with the Mica hint
        // on this build, and the legacy DWMWA_MICA_EFFECT (1029) no longer exists at all.
        Source().Should().Contain("IsWindowsVersionAtLeast(10, 0, 22621)",
            "IsSupported must gate on 22H2 (build 22621), the first build that honours the " +
            "backdrop attribute");
    }

    [Fact]
    public void TheDwmAttributeIdsAndValues_MatchTheDocumentedFinding()
    {
        var source = Source();
        source.Should().Contain("DWMWA_USE_IMMERSIVE_DARK_MODE = 20");
        source.Should().Contain("DWMWA_SYSTEMBACKDROP_TYPE = 38");
        source.Should().Contain("DWMSBT_MAINWINDOW = 2");
        source.Should().Contain("DWMSBT_AUTO = 0");
    }

    [Fact]
    public void TryApply_ReturnsFalseRatherThanThrowingWithNoSupportOrNoHandle()
    {
        var source = Source();
        source.Should().Contain("if (!IsSupported) return false;",
            "an unsupported OS must be a clean false, not an attempted DWM call");
        source.Should().Contain("if (hwnd == IntPtr.Zero) return false;",
            "no platform handle yet (startup, before Opened) must be a clean false so the caller " +
            "can retry rather than crash");
    }

    [Fact]
    public void TryApply_SucceedsOnlyWhenTheBackdropCallItselfReturnsSOk()
    {
        Source().Should().Contain("return hr == 0;",
            "TryApply's return value must reflect whether DWM actually accepted the backdrop " +
            "request, not merely whether the call was attempted");
    }

    [Fact]
    public void Clear_RequestsAutoRatherThanNone()
    {
        Source().Should().Contain("DWMSBT_AUTO",
            "leaving Mica resets the backdrop to AUTO so DWM is free to resume its own default, " +
            "rather than pinning it to a hard NONE");
    }

    /// <summary>
    /// Live-check B4: after perf P3-68 a Windows dark→light→dark flip re-requested MAINWINDOW over
    /// itself and Mica never came back. A theme-variant change must re-apply (clear, then request),
    /// while an unchanged variant must still cost nothing.
    /// </summary>
    [Fact]
    public void Plan_ThemeVariantChange_ReappliesMica()
    {
        Remex.Desktop.Services.MicaBackdrop.Plan(appliedDark: true, dark: false)
            .Should().Be(Remex.Desktop.Services.MicaBackdrop.Request.Reapply, "dark→light must re-apply Mica");
        Remex.Desktop.Services.MicaBackdrop.Plan(appliedDark: false, dark: true)
            .Should().Be(Remex.Desktop.Services.MicaBackdrop.Request.Reapply, "light→dark must re-apply Mica");
    }

    [Fact]
    public void Plan_FirstRequestApplies_UnchangedVariantDoesNothing()
    {
        Remex.Desktop.Services.MicaBackdrop.Plan(appliedDark: null, dark: true)
            .Should().Be(Remex.Desktop.Services.MicaBackdrop.Request.Apply);
        Remex.Desktop.Services.MicaBackdrop.Plan(appliedDark: null, dark: false)
            .Should().Be(Remex.Desktop.Services.MicaBackdrop.Request.Apply);
        Remex.Desktop.Services.MicaBackdrop.Plan(appliedDark: true, dark: true)
            .Should().Be(Remex.Desktop.Services.MicaBackdrop.Request.None,
                "a slider settle with the variant unchanged must not repeat the DWM calls (P3-68)");
        Remex.Desktop.Services.MicaBackdrop.Plan(appliedDark: false, dark: false)
            .Should().Be(Remex.Desktop.Services.MicaBackdrop.Request.None);
    }

    private static string Source() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "MicaBackdrop.cs"));

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
