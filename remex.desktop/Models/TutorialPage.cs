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

/// <summary>
/// Author indices of the tutorial pages something deep-links to. A link names a page, never a
/// carousel position: resolve these through <c>TutorialNavigator.PositionOfPage</c> against the
/// platform-filtered list.
/// </summary>
public static class TutorialPageIds
{
    /// <summary>The Routines page (spec §5.2). The Routines page's "Learn about routines" opens it.</summary>
    public const int Routines = 16;

    /// <summary>The closing "You're all set" page. Always the highest author index.</summary>
    public const int Finish = 17;
}
