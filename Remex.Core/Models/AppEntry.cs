using System;

namespace Remex.Core.Models;

/// <summary>
/// Represents a customizable launcher entry for an application.
/// </summary>
public record AppEntry(
    Guid Id,
    string DisplayName,
    string TargetPath,
    string HexColor,
    string? IconBase64
);
