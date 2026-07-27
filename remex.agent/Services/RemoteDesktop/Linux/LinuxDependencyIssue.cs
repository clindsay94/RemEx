using System.Runtime.Versioning;

namespace Remex.Agent.Services.RemoteDesktop.Linux;

/// <summary>
/// Severity of a detected Linux dependency issue.
/// </summary>
[SupportedOSPlatform("linux")]
public enum LinuxDependencyIssueSeverity
{
    /// <summary>Informational — the feature works but is degraded.</summary>
    Info,

    /// <summary>A non-critical component is missing. Core features still work.</summary>
    Warning,

    /// <summary>A required component is missing. The affected feature will not work.</summary>
    Error,
}

/// <summary>
/// Describes a single missing or misconfigured Linux dependency.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record LinuxDependencyIssue
{
    /// <summary>Component name (e.g., "PipeWire", "libei", "uinput").</summary>
    public required string Component { get; init; }

    /// <summary>Human-readable description of the issue.</summary>
    public required string Description { get; init; }

    /// <summary>Severity level.</summary>
    public required LinuxDependencyIssueSeverity Severity { get; init; }

    /// <summary>
    /// Whether an automated repair action is available.
    /// When true, <see cref="LinuxDependencyRepairService"/> can attempt to fix this issue.
    /// </summary>
    public bool RepairAvailable { get; init; }

    /// <summary>
    /// Brief description of what the repair action will do.
    /// Null when <see cref="RepairAvailable"/> is false.
    /// </summary>
    public string? RepairDescription { get; init; }
}

/// <summary>
/// Encapsulates the result of executing a single repair action.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed record LinuxDependencyRepairResult
{
    /// <summary>The repair action that was attempted.</summary>
    public required LinuxRepairAction Action { get; init; }

    /// <summary>Whether the repair action completed without error.</summary>
    public bool Success { get; init; }

    /// <summary>Standard output / combined output from the repair command.</summary>
    public string? Output { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether a re-evaluation of prerequisites after this repair showed improvement.
    /// Null when not re-evaluated.
    /// </summary>
    public bool? FixedIssue { get; init; }
}
