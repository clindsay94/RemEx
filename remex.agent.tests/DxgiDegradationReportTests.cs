using System.Runtime.Versioning;
using Remex.Agent.Services.RemoteDesktop.Windows;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// The diagnostic report only calls capture degraded after DXGI was probed and failed (RemEx-61no2).
/// </summary>
/// <remarks>
/// DXGI init is deferred to the first capture (RemEx-hmj), so at startup it is always "not available
/// yet". The report read that as "unavailable" and logged a capture-degraded warning at every launch on
/// every machine, which also buried a real DXGI failure among the false ones.
/// </remarks>
[SupportedOSPlatform("windows")]
public class DxgiDegradationReportTests
{
    [Fact]
    public void BeforeTheFirstProbe_NothingIsDegraded()
    {
        Assert.Null(WindowsRemoteDesktopDiagnostics.DescribeDxgiDegradation(probed: false, available: false, reason: null));
    }

    [Fact]
    public void ProbedAndWorking_NothingIsDegraded()
    {
        Assert.Null(WindowsRemoteDesktopDiagnostics.DescribeDxgiDegradation(probed: true, available: true, reason: null));
    }

    [Fact]
    public void ProbedAndFailed_ReportsDegradedWithTheReason()
    {
        var line = WindowsRemoteDesktopDiagnostics.DescribeDxgiDegradation(probed: true, available: false, reason: "E_ACCESSDENIED");

        Assert.NotNull(line);
        Assert.Contains("E_ACCESSDENIED", line);
        Assert.Contains("GDI", line);
    }

    [Fact]
    public void ProbedAndFailedWithoutAReason_StillReportsDegraded()
    {
        var line = WindowsRemoteDesktopDiagnostics.DescribeDxgiDegradation(probed: true, available: false, reason: null);

        Assert.NotNull(line);
        Assert.DoesNotContain("()", line);
    }
}
