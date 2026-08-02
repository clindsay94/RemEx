using System.Globalization;

namespace Remex.Core.Models;

/// <summary>
/// Names a screenshot pushed from the PC to the phone's gallery (RemEx-zvtr).
/// </summary>
/// <remarks>
/// <para>
/// The name ends up in a gallery app the user browses months later, so it has to sort
/// chronologically as TEXT — galleries sort by name as often as by date, and a name that sorts
/// wrongly makes a folder of screenshots unusable exactly when there are enough of them to matter.
/// Zero-padded, most-significant-first, no separators that a sort treats as significant.
/// </para>
/// <para>
/// **ISO-8601's TIME SEPARATOR IS A COLON, WHICH IS ILLEGAL IN A WINDOWS FILENAME.** Reaching for
/// <c>DateTime.ToString("o")</c> or <c>"s"</c> is the obvious move and produces a name the host
/// cannot even write, let alone offer. On Linux a colon is legal, so this would work in development
/// and fail on the platform most users are on — which is the worst shape a bug can have here.
/// </para>
/// </remarks>
public static class ScreenshotFileName
{
    /// <summary>Prefix, so a gallery groups them and a user can search for them.</summary>
    public const string Prefix = "RemEx";

    /// <summary>Extension. PNG because a screenshot of text must not be re-compressed.</summary>
    public const string Extension = ".png";

    /// <summary>
    /// Builds the file name for a screenshot taken at <paramref name="takenAt"/>.
    /// </summary>
    /// <param name="takenAt">When the shot was taken.</param>
    /// <param name="displayLabel">
    /// Optional display identifier for a multi-monitor host, so two shots taken in the same second
    /// from different screens do not collide and the user can tell which is which.
    /// </param>
    /// <remarks>
    /// **FORMATTED WITH THE INVARIANT CULTURE, WHICH IS LOAD-BEARING RATHER THAN TIDY.** A
    /// culture-sensitive format under <c>ar-SA</c> renders Arabic-Indic digits, and under several
    /// locales substitutes a non-Gregorian calendar — producing a file name that neither sorts with
    /// its siblings nor matches what the rest of the app expects, on the machines of exactly the
    /// users least likely to be able to report it clearly.
    /// </remarks>
    public static string ForTimestamp(DateTimeOffset takenAt, string? displayLabel = null)
    {
        // Colons and spaces deliberately absent: colons are illegal on Windows, and a space forces
        // every shell command a user might later run to be quoted.
        var stamp = takenAt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        var suffix = SanitizeLabel(displayLabel);

        return suffix is null
            ? $"{Prefix}_{stamp}{Extension}"
            : $"{Prefix}_{stamp}_{suffix}{Extension}";
    }

    /// <summary>
    /// Reduces a display label to something safe to put in a file name.
    /// </summary>
    /// <remarks>
    /// A monitor's name comes from the OS and can contain anything — <c>\\.\DISPLAY1</c> on Windows,
    /// or a manufacturer string with punctuation. Passing that through would produce a path
    /// separator inside a file name, which is the traversal shape
    /// <c>FilePathValidation.IsValidFileName</c> exists to refuse. Only ASCII letters, digits and
    /// dashes survive; anything else is dropped rather than substituted, so two different labels
    /// cannot collapse into the same underscore soup.
    /// </remarks>
    private static string? SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        Span<char> buffer = stackalloc char[Math.Min(label.Length, MaxLabelLength)];
        var written = 0;

        foreach (var ch in label)
        {
            if (written == buffer.Length) break;
            if (char.IsAsciiLetterOrDigit(ch) || ch == '-') buffer[written++] = ch;
        }

        return written == 0 ? null : new string(buffer[..written]);
    }

    /// <summary>Longest display label kept, so a verbose monitor name cannot dominate the name.</summary>
    private const int MaxLabelLength = 16;
}
