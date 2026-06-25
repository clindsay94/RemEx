using System;

namespace Remex.Desktop.Models;

/// <summary>Platform flags controlling which OS sees a tutorial page.</summary>
[Flags]
public enum PlatformFlags
{
    None    = 0,
    Windows = 1,
    Linux   = 2,
    Android = 4,
    All     = Windows | Linux | Android
}

/// <summary>Describes a single tutorial page and which platforms show it.</summary>
public record TutorialPage(int PageIndex, string Title, string Description, PlatformFlags SupportedPlatforms);
