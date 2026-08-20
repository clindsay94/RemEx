using System;
using System.Linq;
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
    /// This build's own identity, e.g. "39b0b09", or "39b0b09+a3f1" when it was built from a working
    /// tree with uncommitted changes. Empty when the assembly carries no stamp.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DIFFERENT QUESTION FROM <see cref="Display"/>, which is why it is a separate value rather
    /// than more characters appended to the version. The version says which RELEASE this is; the
    /// build id says which BUILD. Both heads sat on 2.4.0 for months, so during a review session the
    /// version answered nothing at all and "is the fix in this binary?" had to be settled by
    /// comparing a file timestamp against a commit timestamp — which was done twice on this branch
    /// and got the wrong answer once.
    /// </para>
    /// <para>
    /// THE '+' IS THE PART TO READ. It means the binary was built from uncommitted work, so the
    /// commit it names is where the build STARTED, not what it contains. See build/BuildId.targets
    /// for exactly how much the four characters after it distinguish — less than they look like.
    /// </para>
    /// </remarks>
    public static string BuildId { get; } = ResolveBuildId(typeof(AppVersion).Assembly);

    /// <summary>
    /// Reads the build stamp written into <paramref name="assembly"/> by <c>build/BuildId.targets</c>.
    /// Public for tests; production code should read <see cref="BuildId"/>.
    /// </summary>
    /// <remarks>
    /// Returns empty for both "no stamp at all" and the literal "unknown" the targets file writes
    /// when git is unavailable. Callers only ever need to decide whether there is something worth
    /// showing, and an About row reading "unknown" next to a real version is worse than no row.
    /// </remarks>
    public static string ResolveBuildId(Assembly assembly)
    {
        var stamp = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "RemexBuildId", StringComparison.Ordinal))
            ?.Value;

        if (string.IsNullOrWhiteSpace(stamp)) return string.Empty;

        var trimmed = stamp.Trim();
        return string.Equals(trimmed, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    /// <summary>
    /// Resolves the display version of <paramref name="assembly"/>. Public for tests; production
    /// code should read <see cref="Display"/>, which resolves this assembly once.
    /// </summary>
    public static string Resolve(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var normalized = Normalize(informational);
        if (!string.IsNullOrEmpty(normalized)) return normalized;

        var version = assembly.GetName().Version;
        if (version is null) return string.Empty;

        // Version.ToString(3) throws when the third component is undefined (Build is -1 for a
        // two-part version such as "2.4"), so fall back a component rather than crash the About page.
        return version.Build >= 0 ? version.ToString(3) : version.ToString(2);
    }

    /// <summary>
    /// Reduces an already-resolved version STRING to the same display form, e.g. "2.4.0.0" and
    /// "2.4.0+abc123" both to "2.4.0".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists so the version a REMOTE host reports over the wire can be shown in the form this
    /// machine shows its own. The About page lists both one divider apart, and after RemEx-nll1
    /// fixed only the local row the same machine was labelled "2.4.0" and "2.4.0.0" on one screen
    /// (RemEx-8jzu). Sharing this rather than trimming again at the call site is the whole point:
    /// a third copy of "what a version looks like here" is how the two rows diverged in the first
    /// place.
    /// </para>
    /// <para>
    /// A value that is not a version at all is returned unchanged rather than blanked — the host
    /// sends the literal "unknown" when it cannot determine its own, and callers test for that. A
    /// prerelease tag such as "2.5.0-rc1" is left alone for the same reason.
    /// </para>
    /// <para>
    /// ONE CONSEQUENCE WORTH KNOWING: a four-part <c>&lt;Version&gt;</c> in Directory.Build.props
    /// would now be reduced here rather than surfacing as-is, so the three-component assertion in
    /// AppVersionTests would no longer catch it. That is this method doing its job, but it means the
    /// tripwire moved — the version declared in the props file is no longer visible in what the
    /// About page shows.
    /// </para>
    /// </remarks>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var plus = raw.IndexOf('+');
        var trimmed = (plus >= 0 ? raw[..plus] : raw).Trim();
        if (trimmed.Length == 0) return string.Empty;

        // Not parseable as a version (e.g. "unknown", or a prerelease tag): hand it back untouched
        // rather than guessing. Blanking it would turn a diagnosable value into no value at all.
        if (!Version.TryParse(trimmed, out var parsed)) return trimmed;

        return parsed.Build >= 0 ? parsed.ToString(3) : parsed.ToString(2);
    }
}
