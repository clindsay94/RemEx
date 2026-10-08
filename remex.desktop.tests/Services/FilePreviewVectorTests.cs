using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Remex.Desktop.Services.FilePreview;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// The PC half of the preview parity check (file browser redesign, 2026-10-08). <c>file-preview-vectors.json</c>
/// holds what the classifier, the decoder and the tokenizer answer for a set of inputs; the phone's Kotlin twins
/// assert against the same file (<c>FilePreviewVectorsTest</c>), and the Android copy must stay byte-identical,
/// so a rule changed on one platform and not the other fails here or there.
/// </summary>
public sealed class FilePreviewVectorTests
{
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    private static string FixturePath() => Path.Combine(RepoRoot(), "remex.desktop.tests", "Fixtures", "file-preview-vectors.json");

    private static JsonElement Section(string name)
    {
        var doc = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        var section = doc.RootElement.GetProperty(name);
        section.GetArrayLength().Should().BeGreaterThan(3, "an empty section would pass vacuously");
        return section;
    }

    [Fact]
    public void TheAndroidCopyIsByteIdentical()
    {
        var android = Path.Combine(RepoRoot(), "remex.android", "app", "src", "test", "resources", "file-preview-vectors.json");
        File.Exists(android).Should().BeTrue(android);
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(android)))
            .Should().Be(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FixturePath()))));
    }

    [Fact]
    public void TheClassifierMatchesTheVectors()
    {
        foreach (var v in Section("classify").EnumerateArray())
            PreviewClassifier.Classify(v.GetProperty("name").GetString()!, v.GetProperty("isDirectory").GetBoolean()).ToString()
                .Should().Be(v.GetProperty("kind").GetString(), v.GetProperty("name").GetString());
    }

    [Fact]
    public void TheLanguagesMatchTheVectors()
    {
        foreach (var v in Section("languages").EnumerateArray())
            SyntaxTokenizer.LanguageFor(v.GetProperty("name").GetString()!).ToString()
                .Should().Be(v.GetProperty("language").GetString(), v.GetProperty("name").GetString());
    }

    [Fact]
    public void TheTokenizerMatchesTheVectors()
    {
        foreach (var v in Section("tokenize").EnumerateArray())
        {
            var line = v.GetProperty("line").GetString()!;
            var language = Enum.Parse<SyntaxLanguage>(v.GetProperty("language").GetString()!);
            var expected = v.GetProperty("spans").EnumerateArray()
                .Select(s => new SyntaxSpan(s[0].GetInt32(), s[1].GetInt32(), Enum.Parse<SyntaxKind>(s[2].GetString()!)));
            SyntaxTokenizer.Tokenize(line, language).Should().Equal(expected, line);
        }
    }

    [Fact]
    public void TheDecoderMatchesTheVectors()
    {
        foreach (var v in Section("decode").EnumerateArray())
        {
            var bytes = Convert.FromHexString(v.GetProperty("hex").GetString()!);
            var because = v.GetProperty("hex").GetString();
            TextPreviewDecoder.Decode(bytes, v.GetProperty("startsMidFile").GetBoolean(), v.GetProperty("endsMidFile").GetBoolean())
                .Should().Be(v.GetProperty("text").GetString(), because);
            TextPreviewDecoder.CompleteUtf8Length(bytes).Should().Be(v.GetProperty("completeUtf8Length").GetInt32(), because);
            TextPreviewDecoder.LooksBinary(bytes).Should().Be(v.GetProperty("looksBinary").GetBoolean(), because);
        }
    }
}
