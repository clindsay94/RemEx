using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Localization;

/// <summary>
/// Guards against a localization key that code asks for but no <c>.resx</c> file defines.
/// </summary>
/// <remarks>
/// <see cref="Remex.Desktop.Services.LocalizationService"/>'s indexer ends in <c>?? key</c>, so a
/// key missing from every file is not an exception — it renders the developer string on screen, in
/// every language. RemEx-2s91 shipped exactly that: users saw the literal text
/// <c>TaskManager_KillFailed</c>.
/// <para>
/// A cross-locale parity test cannot catch this. Parity is never broken, because the key is absent
/// from all nine files equally. Referenced-but-undefined is a separate axis, and this is it
/// (RemEx-fxkg).
/// </para>
/// <para>
/// The reverse direction — keys defined but never referenced — is deliberately NOT asserted here.
/// It is a different judgement (an unreferenced key can be a genuine gap rather than dead weight)
/// and there is a real backlog of them, so a test asserting none would simply fail. That cleanup is
/// tracked by RemEx-b5kx.
/// </para>
/// <para>
/// KNOWN LIMITATIONS, both deliberate. This scans raw text, so a key named inside a comment counts
/// as a reference — which fails safe (it can only demand that a key exists, never hide a missing
/// one) but does mean naming a hypothetical key in a comment will fail this test. And keys built by
/// concatenation, such as <c>"Splash_Style_" + id</c> or the enum converter's
/// <c>$"{type}_{value}"</c>, are invisible to it: those cannot be checked statically at all, and
/// are the one remaining way to reach the <c>?? key</c> fallback undetected.
/// </para>
/// </remarks>
public class LocalizationKeyReferenceTests
{
    /// <summary>
    /// Every way a localization key is named in source. Group 1 must be the key.
    /// </summary>
    /// <remarks>
    /// The XAML pattern accepts ANY namespace prefix on purpose: this repo uses both
    /// <c>{local:Localize}</c> and <c>{conv:Localize}</c>, and a pattern hard-coded to the first
    /// silently ignores 116 references — the same blind spot this test exists to remove.
    /// </remarks>
    private static readonly Regex[] ReferencePatterns =
    [
        // LocalizationService.Instance["Key"] — \s* because long lines get wrapped.
        new(@"Instance\[\s*""([A-Za-z0-9_]+)""\s*\]", RegexOptions.Compiled),

        // {local:Localize Key} / {conv:Localize Key} in XAML.
        new(@"\{\s*[A-Za-z_][A-Za-z0-9_]*:Localize\s+([A-Za-z0-9_]+)", RegexOptions.Compiled),

        // The generated Strings.Key accessor.
        new(@"(?<![A-Za-z0-9_.])Strings\.([A-Z][A-Za-z0-9_]*)", RegexOptions.Compiled),

        // Confirmation keys reach ConfirmAsync and CommandPaletteEntry as plain positional
        // strings, so they carry no compiler check at all and are exactly as easy to typo.
        new(@"""(Confirm_[A-Za-z0-9_]+)""", RegexOptions.Compiled),
    ];

    /// <summary>Members of the generated <c>Strings</c> class that are not resource keys.</summary>
    private static readonly HashSet<string> NotKeys = new(StringComparer.Ordinal)
    {
        "ResourceManager", "Culture",
    };

    [Fact]
    public void EveryKeyReferencedInCode_IsDefinedInStringsResx()
    {
        var defined = LoadBaseResxKeys();
        defined.Should().NotBeEmpty("the base Strings.resx must be readable for this test to mean anything");

        var referenced = CollectReferencedKeys();
        referenced.Should().NotBeEmpty("source must contain localization references for this test to mean anything");

        var missing = referenced
            .Where(pair => !defined.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}  (referenced in {string.Join(", ", pair.Value.Take(3))})")
            .ToList();

        missing.Should().BeEmpty(
            "a key defined in no .resx file is shown to the user as its own name, in every " +
            "language, because LocalizationService's indexer ends in '?? key'");
    }

    private static SortedDictionary<string, List<string>> CollectReferencedKeys()
    {
        var found = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in ReferencePatterns)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    var key = match.Groups[1].Value;
                    if (NotKeys.Contains(key))
                        continue;

                    if (!found.TryGetValue(key, out var where))
                        found[key] = where = new List<string>();

                    var relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
                    if (!where.Contains(relative))
                        where.Add(relative);
                }
            }
        }

        return found;
    }

    private static IEnumerable<string> SourceFiles()
    {
        foreach (var project in new[] { "remex.desktop", "remex.agent" })
        {
            var root = Path.Combine(RepoRoot(), project);
            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (extension is not (".cs" or ".axaml" or ".xaml"))
                    continue;

                // Build output would double-count, and the generated accessor file declares the
                // very members the Strings.Key pattern looks for.
                var normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/", StringComparison.Ordinal)
                    || normalized.Contains("/obj/", StringComparison.Ordinal)
                    || normalized.EndsWith("Strings.Designer.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static HashSet<string> LoadBaseResxKeys()
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization", "Strings.resx");
        File.Exists(path).Should().BeTrue($"the base resx must exist at {path}");

        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Select(d => (string?)d.Attribute("name"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The repository root, resolved from THIS source file rather than from the test assembly.
    /// </summary>
    /// <remarks>
    /// Walking up from the assembly couples the test to build output living inside the repo, so
    /// building with <c>--artifacts-path</c> elsewhere breaks it with an error that says nothing
    /// about the change that caused it — see RemEx-6i1l, where exactly that happened.
    /// </remarks>
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        // <repo>/remex.desktop.tests/Localization/ThisFile.cs -> <repo>
        var directory = Path.GetDirectoryName(thisSourceFile)!;
        return Path.GetFullPath(Path.Combine(directory, "..", ".."));
    }

    /// <summary>
    /// Catches <c>string.Format(Instance["Key"], arg)</c> where the key's value has no placeholder.
    /// </summary>
    /// <remarks>
    /// This is the trap RemEx-udq9 exists to settle. <see cref="string.Format(string, object?)"/>
    /// against a placeholder-free string SILENTLY DROPS the argument — no exception, no warning —
    /// so the user is shown a sentence with the filename, count or error detail simply missing.
    /// <para>
    /// The naming convention cannot be relied on to prevent it, which is why this check exists
    /// rather than a rename. Six keys currently end in <c>Format</c> while carrying no placeholder,
    /// and one of them — <c>Status_InvalidMessageFormat</c>, "Invalid message format from PC" —
    /// uses the word as part of its MEANING. A rule based on the suffix would demand renaming that
    /// one too, and would still not stop the mistake being made against a key named anything else.
    /// Detecting the actual call shape makes the name irrelevant.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoStringFormatCall_TargetsAKeyWithoutAPlaceholder()
    {
        var defined = LoadBaseResxValues();
        var offenders = new List<string>();
        var checkedSites = 0;

        var pattern = new Regex(
            @"string\.Format\(\s*(?:[A-Za-z0-9_.]*?)Instance\[\s*""([A-Za-z0-9_]+)""\s*\]",
            RegexOptions.Compiled);

        foreach (var file in SourceFiles())
        {
            if (!file.EndsWith(".cs", StringComparison.Ordinal))
                continue;

            // Collapsed so a call wrapped across lines by the formatter still matches.
            var text = Regex.Replace(File.ReadAllText(file), @"\s+", " ");
            foreach (Match match in pattern.Matches(text))
            {
                var key = match.Groups[1].Value;
                if (!defined.TryGetValue(key, out var value))
                    continue;   // undefined keys are the other test's job

                checkedSites++;
                if (PlaceholderIndexes(value).Count == 0)
                {
                    var relative = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');
                    offenders.Add($"{key} = \"{value}\"  formatted at {relative}");
                }
            }
        }

        checkedSites.Should().BeGreaterThan(0,
            "the pattern must actually match this codebase's string.Format calls, or this test " +
            "passes vacuously");

        offenders.Should().BeEmpty(
            "string.Format against a placeholder-free string drops the argument silently, so the " +
            "user sees a sentence with the filename, count or reason missing");
    }

    /// <summary>
    /// Argument indexes in a .NET composite format string: <c>{index[,align][:format]}</c>.
    /// </summary>
    /// <remarks>
    /// Hand-scanned rather than regexed because <c>{{</c> and <c>}}</c> are literal braces. Note
    /// also that a naive search for the substring <c>"{0}"</c> is wrong and was the first version
    /// of this check: <c>"Zoom: {0:F1}×"</c> carries a format specifier and would be reported as
    /// having no placeholder at all.
    /// </remarks>
    private static HashSet<string> PlaceholderIndexes(string value)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < value.Length;)
        {
            if (value[i] == '{' && i + 1 < value.Length && value[i + 1] == '{') { i += 2; continue; }
            if (value[i] == '}' && i + 1 < value.Length && value[i + 1] == '}') { i += 2; continue; }
            if (value[i] == '{')
            {
                var close = value.IndexOf('}', i + 1);
                if (close < 0) break;
                var head = value.Substring(i + 1, close - i - 1).Split(',', ':')[0];
                if (head.Length > 0 && head.All(char.IsAsciiDigit))
                    found.Add(head);
                i = close + 1;
                continue;
            }
            i++;
        }
        return found;
    }

    /// <summary>
    /// Every TRANSLATION of a key that has placeholders must carry the same placeholder indexes.
    /// </summary>
    /// <remarks>
    /// <see cref="NoStringFormatCall_TargetsAKeyWithoutAPlaceholder"/> reads only the neutral
    /// <c>Strings.resx</c>, so it proves English is formattable and says nothing about the other
    /// eight files. A translator who drops <c>{0}</c> produces a sentence with the number, filename
    /// or reason silently missing — <see cref="string.Format(string, object?)"/> does not complain —
    /// and the damage is visible ONLY in that language, which is precisely the class of defect an
    /// English-reading reviewer cannot see.
    /// <para>
    /// This became load-bearing with RemEx-si0h, which moved the canvas selection counter from a
    /// bare adjective to <c>Canvas_SelectedCountFormat</c>. Nine files now each have to carry the
    /// placeholder, and each locale deliberately places it differently: Polish and Ukrainian lead
    /// with an impersonal verb, English trails the adjective.
    /// </para>
    /// <para>
    /// A locale that simply omits the key is NOT an offender here — that is the parity question,
    /// and conflating the two would make this test fail for a reason it does not describe.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTranslationOfAFormatKey_KeepsThePlaceholders()
    {
        var baseValues = LoadBaseResxValues();
        var formatKeys = baseValues
            .Where(kv => PlaceholderIndexes(kv.Value).Count > 0)
            .ToDictionary(kv => kv.Key, kv => PlaceholderIndexes(kv.Value), StringComparer.Ordinal);

        formatKeys.Should().NotBeEmpty("this codebase does use composite format strings");

        var offenders = new List<string>();
        var localeDirectory = Path.Combine(RepoRoot(), "remex.desktop", "Localization");

        foreach (var path in Directory.GetFiles(localeDirectory, "Strings.*.resx"))
        {
            var locale = Path.GetFileNameWithoutExtension(path).Replace("Strings.", string.Empty);
            var values = XDocument.Load(path)
                .Root!
                .Elements("data")
                .Where(d => d.Element("value") is not null && (string?)d.Attribute("name") is not null)
                .ToDictionary(
                    d => (string)d.Attribute("name")!,
                    d => d.Element("value")!.Value,
                    StringComparer.Ordinal);

            foreach (var (key, expected) in formatKeys)
            {
                if (!values.TryGetValue(key, out var translated))
                    continue;   // missing key is the parity question, not this one

                var actual = PlaceholderIndexes(translated);
                if (!actual.SetEquals(expected))
                {
                    offenders.Add(
                        $"{locale}/{key}: expected placeholders {{{string.Join(",", expected.OrderBy(i => i))}}} " +
                        $"but found {{{string.Join(",", actual.OrderBy(i => i))}}} in \"{translated}\"");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a translation that loses a placeholder silently drops the argument, and only speakers " +
            "of that language ever see the gap");
    }

    private static Dictionary<string, string> LoadBaseResxValues()
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization", "Strings.resx");
        return XDocument.Load(path)
            .Root!
            .Elements("data")
            .Where(d => d.Element("value") is not null && (string?)d.Attribute("name") is not null)
            .ToDictionary(
                d => (string)d.Attribute("name")!,
                d => d.Element("value")!.Value,
                StringComparer.Ordinal);
    }
}
