using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
}
