using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>
/// Live Handshake's strings (RemEx-8g6n0.2). <c>LocalizedHandshakeText</c> builds its plural keys by
/// concatenation, which <c>LocalizationKeyReferenceTests</c> cannot see, so the keys
/// it asks for are pinned here: present in all nine files, and every template formats with the
/// arguments the splash passes.
/// </summary>
public class LocalizedHandshakeTextTests
{
    private static readonly (string Key, int Args)[] Keys =
    {
        ("Splash_Style_LiveHandshake", 0),
        ("Splash_LiveHandshake_Paired_One", 1),
        ("Splash_LiveHandshake_Paired_Few", 1),
        ("Splash_LiveHandshake_Paired_Many", 1),
        ("Splash_LiveHandshake_Paired_Other", 1),
        ("Splash_LiveHandshake_NonePaired", 0),
        ("Splash_LiveHandshake_ListeningOnPort", 1),
        ("Splash_LiveHandshake_Listening", 0),
        ("Splash_LiveHandshake_IsLinked", 1),
        ("Splash_LiveHandshake_Opening", 0),
        ("Splash_LiveHandshake_NodeLinked", 0),
    };

    public static TheoryData<string> ResxFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "remex.desktop", "Localization"), "Strings*.resx"))
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Fact]
    public void AllNineLanguagesAreChecked() => ResxFiles().Should().HaveCount(9);

    [Theory]
    [MemberData(nameof(ResxFiles))]
    public void TheRetiredStatusLineKeysAreGone(string file)
    {
        // RemEx-8g6n0.3: the single status line became the console; its keys must not linger unused.
        var names = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "Localization", file)).Root!
            .Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet();
        foreach (var retired in new[] { "Splash_LiveHandshake_Starting", "Splash_LiveHandshake_Pinging_Other",
                     "Splash_LiveHandshake_LinkedCount", "Splash_LiveHandshake_LinkedTo", "Splash_LiveHandshake_NotAnswering" })
            names.Should().NotContain(retired, file);
    }

    [Theory]
    [MemberData(nameof(ResxFiles))]
    public void EveryKeyIsTranslatedAndFormats(string file)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "Localization", file));
        var values = doc.Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

        foreach (var (key, args) in Keys)
        {
            values.Should().ContainKey(key, $"{file} must carry {key}");
            var template = values[key];
            template.Should().NotBeNullOrWhiteSpace($"{file}:{key}");
            var format = () => string.Format(CultureInfo.InvariantCulture, template, Enumerable.Range(1, 2).Cast<object>().ToArray());
            format.Should().NotThrow($"{file}:{key} is a format string");
            for (int i = 0; i < args; i++)
                template.Should().Contain("{" + i + "}", $"{file}:{key} must place argument {i}");
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
