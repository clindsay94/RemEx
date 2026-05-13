using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Remex.Host.Services.RemoteDesktop.Linux;

/// <summary>
/// Categories of repair action that may be required.
/// </summary>
public enum LinuxRepairActionKind
{
    /// <summary>Install a missing package via the system package manager.</summary>
    InstallPackage,

    /// <summary>Start or restart a user-level systemd service.</summary>
    RestartUserService,

    /// <summary>Add the current user to the <c>input</c> group for uinput access.</summary>
    AddUserToInputGroup,

    /// <summary>Set a udev rule to grant write access to <c>/dev/uinput</c>.</summary>
    AddUinputUdevRule,

    /// <summary>Manual step that requires user action outside the app.</summary>
    Manual,
}

/// <summary>
/// A single concrete action in a repair plan.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record LinuxRepairAction(
    LinuxRepairActionKind Kind,
    string Description,
    string? Command = null,
    bool RequiresElevation = false);

/// <summary>
/// An ordered repair plan produced by <see cref="LinuxRemoteDesktopPrerequisites"/>
/// and consumed by <see cref="LinuxDependencyRepairService"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record LinuxPrerequisiteRepairPlan
{
    public IReadOnlyList<LinuxRepairAction> Actions { get; init; } = Array.Empty<LinuxRepairAction>();

    /// <summary>
    /// True when at least one repair action is available and the host is on an
    /// Arch-family distribution where automated package installs are supported.
    /// </summary>
    public bool HasAutomatedRepair { get; init; }

    /// <summary>True when any repair action requires elevated (root) access.</summary>
    public bool RequiresElevation => Actions.Count > 0 && ((List<LinuxRepairAction>)Actions).Exists(a => a.RequiresElevation);

    public static LinuxPrerequisiteRepairPlan Empty { get; } = new();
}
