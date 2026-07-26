using System;
using System.Collections.Generic;
using FluentAssertions;
using Remex.Core.Models;
using Remex.Desktop.ViewModels;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// Covers <see cref="TaskManagerViewModel.ConfirmedTargetStillPresent"/>, the guard that stops a
/// confirmed kill from landing on a reused PID (RemEx-2s91). The confirmation dialog can sit open
/// indefinitely while process polling continues, so the process the user agreed to end may be gone -
/// and its PID handed to something else - by the time they click through.
/// </summary>
public class TaskManagerKillTargetTests
{
    private static ProcessInfo Proc(int id, string name, string path = "") =>
        new() { Id = id, Name = name, FilePath = path };

    [Fact]
    public void UnchangedTarget_IsStillPresent()
    {
        var confirmed = Proc(1234, "notepad", @"C:\Windows\notepad.exe");
        var current = new List<ProcessInfo> { Proc(9, "other"), confirmed };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeTrue();
    }

    [Fact]
    public void TargetGoneEntirely_IsNotPresent()
    {
        var confirmed = Proc(1234, "notepad");
        var current = new List<ProcessInfo> { Proc(9, "other") };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeFalse();
    }

    /// <summary>The defect this guard exists for: same PID, different program.</summary>
    [Fact]
    public void PidReusedByADifferentProgram_IsNotPresent()
    {
        var confirmed = Proc(1234, "notepad", @"C:\Windows\notepad.exe");
        var current = new List<ProcessInfo> { Proc(1234, "cmd", @"C:\Windows\System32\cmd.exe") };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeFalse();
    }

    /// <summary>
    /// Same PID and same name, but a different executable on disk - a different program that merely
    /// shares a common file name, which is exactly how a malicious or accidental impostor looks.
    /// </summary>
    [Fact]
    public void PidReusedBySameNameAtADifferentPath_IsNotPresent()
    {
        var confirmed = Proc(1234, "updater", @"C:\Program Files\Vendor\updater.exe");
        var current = new List<ProcessInfo> { Proc(1234, "updater", @"C:\Users\Public\Temp\updater.exe") };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeFalse();
    }

    [Fact]
    public void EmptyList_IsNotPresent()
    {
        TaskManagerViewModel
            .ConfirmedTargetStillPresent(Proc(1234, "notepad"), Array.Empty<ProcessInfo>())
            .Should().BeFalse();
    }

    /// <summary>
    /// A blank FilePath means the host could not read it, not that the paths differ. Comparing
    /// against it would refuse every kill of a protected process, so the check must fall back to
    /// Id + Name when either side is blank.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Windows\notepad.exe", "")]
    [InlineData("", @"C:\Windows\notepad.exe")]
    [InlineData("", "")]
    public void UnreadableFilePathOnEitherSide_FallsBackToIdAndName(string confirmedPath, string currentPath)
    {
        var confirmed = Proc(1234, "notepad", confirmedPath);
        var current = new List<ProcessInfo> { Proc(1234, "notepad", currentPath) };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeTrue();
    }

    /// <summary>
    /// Casing is compared exactly. Both values come from the same host enumeration so a case-only
    /// difference should never occur in practice; if it somehow does, refusing is the safe answer,
    /// and on Linux two paths differing only in case really are two different files.
    /// </summary>
    [Fact]
    public void CaseOnlyDifferenceInName_IsTreatedAsAChange()
    {
        var confirmed = Proc(1234, "notepad");
        var current = new List<ProcessInfo> { Proc(1234, "NOTEPAD") };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeFalse();
    }

    [Fact]
    public void CaseOnlyDifferenceInFilePath_IsTreatedAsAChange()
    {
        var confirmed = Proc(1234, "notepad", @"C:\Windows\notepad.exe");
        var current = new List<ProcessInfo> { Proc(1234, "notepad", @"C:\Windows\NOTEPAD.EXE") };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeFalse();
    }

    /// <summary>
    /// A real host cannot report the same PID twice, but the guard should still behave predictably if
    /// it ever did: the first matching entry decides, so a duplicated PID whose first occurrence
    /// contradicts the confirmed target is refused rather than rescued by a later, matching one.
    /// Pinned so the answer is deliberate rather than an accident of enumeration order.
    /// </summary>
    [Fact]
    public void DuplicatePidEntries_FirstMatchDecides()
    {
        var confirmed = Proc(1234, "notepad", @"C:\Windows\notepad.exe");
        var current = new List<ProcessInfo>
        {
            Proc(1234, "notepad", @"C:\Users\Public\notepad.exe"),
            Proc(1234, "notepad", @"C:\Windows\notepad.exe"),
        };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeFalse();
    }

    /// <summary>
    /// Two processes can legitimately share a name (several chrome.exe), so the guard must key on the
    /// PID and not simply find the first entry with a matching name.
    /// </summary>
    [Fact]
    public void SameNameAtOtherPids_DoesNotSatisfyTheCheck()
    {
        var confirmed = Proc(1234, "chrome", @"C:\chrome.exe");
        var current = new List<ProcessInfo>
        {
            Proc(1, "chrome", @"C:\chrome.exe"),
            Proc(2, "chrome", @"C:\chrome.exe"),
        };

        TaskManagerViewModel.ConfirmedTargetStillPresent(confirmed, current).Should().BeFalse();
    }
}
