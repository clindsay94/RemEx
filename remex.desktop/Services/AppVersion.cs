using System;
using System.Reflection;

namespace Remex.Desktop.Services;

/// <summary>
/// Resolves this build's own version for display, in the same three-part form the Android About
/// screen shows (<c>BuildConfig.VERSION_NAME</c>, e.g. "2.4.0"), so one release is not labelled two
/// different ways on the two platforms.
/// </summary>
/// <remarks>
/// Prefers <see cref="AssemblyInformationalVersionAttribute"/>, which carries the
/// <c>&lt;Version&gt;</c> declared in <c>Directory.Build.props</c> verbatim ("2.4.0"), over
/// <see cref="AssemblyName.Version"/>, which is always widened to four components ("2.4.0.0") —
/// the trailing ".0" is noise to a non-technical user. The SDK appends "+&lt;git sha&gt;" to the
/// informational version (<c>IncludeSourceRevisionInInformationalVersion</c>, on by default since
/// .NET 8), so everything from the '+' onward is trimmed for the same reason.
/// </remarks>
public static class AppVersion
{
    /// <summary>
    /// The running build's version for display, e.g. "2.4.0". Empty when neither the informational
    /// version nor the assembly version can be resolved, so each call site keeps ownership of what
    /// to show in that case.
    /// </summary>
    public static string Display { get; } = Resolve(typeof(AppVersion).Assembly);

    /// <summary>
    /// Resolves the display version of <paramref name="assembly"/>. Public for tests; production
    /// code should read <see cref="Display"/>, which resolves this assembly once.
    /// </summary>
    public static string Resolve(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            var trimmed = plus >= 0 ? informational[..plus] : informational;
            if (!string.IsNullOrEmpty(trimmed)) return trimmed;
        }

        var version = assembly.GetName().Version;
        if (version is null) return string.Empty;

        // Version.ToString(3) throws when the third component is undefined (Build is -1 for a
        // two-part version such as "2.4"), so fall back a component rather than crash the About page.
        return version.Build >= 0 ? version.ToString(3) : version.ToString(2);
    }
}
