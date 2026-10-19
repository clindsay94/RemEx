using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Remex.Desktop.Controls.Splash;
using Xunit;

namespace Remex.Desktop.Tests.Controls;

/// <summary>The "Random" splash: never the same one twice in a row, a forgiving last-played record, and its picker/label wiring.</summary>
public class SplashRotationTests : IDisposable
{
    private static readonly string[] Real = ["LiveHandshake", "RemexCommand", "CosmicZoom", "Pong"];
    private static readonly string[] LocaleSuffixes = ["", ".es", ".fr", ".hi", ".id", ".pl", ".pt-BR", ".tr", ".uk"];

    public SplashRotationTests() => Cleanup();

    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        try { File.Delete(SplashRotation.FilePath); } catch (IOException) { }
    }

    [Fact]
    public void Playable_IsTheFourRealStyles_WithoutRandom()
    {
        SplashRotation.Playable.Should().Equal(Real);
    }

    [Theory]
    [InlineData("LiveHandshake")]
    [InlineData("RemexCommand")]
    [InlineData("CosmicZoom")]
    [InlineData("Pong")]
    public void Next_NeverRepeatsThePreviousStyle_AndReachesAllTheOthers(string previous)
    {
        var rng = new Random(12345);
        var seen = new HashSet<string>();

        for (var i = 0; i < 500; i++)
        {
            var next = SplashRotation.Next(previous, rng);
            next.Should().NotBe(previous);
            seen.Add(next);
        }

        seen.Should().BeEquivalentTo(Real.Where(id => id != previous));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Random")]
    [InlineData("Bogus")]
    [InlineData("")]
    public void Next_WithNoUsablePrevious_PicksAnyPlayableStyle(string? previous)
    {
        var rng = new Random(7);
        var seen = new HashSet<string>();

        for (var i = 0; i < 500; i++)
        {
            var next = SplashRotation.Next(previous, rng);
            SplashRotation.Playable.Should().Contain(next);
            seen.Add(next);
        }

        seen.Should().BeEquivalentTo(Real);
    }

    [Fact]
    public async Task RememberThenReadLast_RoundTrips()
    {
        await SplashRotation.RememberAsync("Pong");

        SplashRotation.ReadLast().Should().Be("Pong");
    }

    [Fact]
    public void ReadLast_IsNullForAMissingFile()
    {
        File.Exists(SplashRotation.FilePath).Should().BeFalse();

        SplashRotation.ReadLast().Should().BeNull();
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("Random")]
    [InlineData("")]
    public void ReadLast_IsNullForAnUnknownValue(string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SplashRotation.FilePath)!);
        File.WriteAllText(SplashRotation.FilePath, contents);

        SplashRotation.ReadLast().Should().BeNull();
    }

    [Fact]
    public void Remember_IgnoresAnIdThatIsNotPlayable()
    {
        SplashRotation.Remember("Random");

        File.Exists(SplashRotation.FilePath).Should().BeFalse();
    }

    [Fact]
    public void EveryPickerStyle_HasALabelInAllNineLanguages()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "ViewModels", "CustomizationViewModel.cs"));
        var list = Regex.Match(source, @"AvailableSplashStyles \{ get; \} = new\(\)\s*\{(?<ids>[^}]*)\}");
        list.Success.Should().BeTrue("the picker list must still be a literal this test can read");
        var ids = Regex.Matches(list.Groups["ids"].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

        ids.Should().NotBeEmpty();
        ids.Should().StartWith(Real, "the four real styles keep their order; Random is appended last");
        ids.Last().Should().Be(SplashRotation.Random);

        foreach (var suffix in LocaleSuffixes)
        {
            var doc = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "Localization", $"Strings{suffix}.resx"));
            foreach (var id in ids)
            {
                var value = doc.Root!.Elements("data")
                    .FirstOrDefault(d => (string?)d.Attribute("name") == "Splash_Style_" + id)?.Element("value")?.Value;
                value.Should().NotBeNullOrWhiteSpace($"Splash_Style_{id} must exist in Strings{suffix}.resx");
            }
        }
    }

    [Fact]
    public void RestartClearsTheRandomPick_BeforeBuildingTheVariant()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Controls", "Splash", "SkiaSplashControl.cs"));

        var restart = Regex.Match(source, @"public void Restart\(\)\s*\{(?<body>.*?)InvalidateVisual", RegexOptions.Singleline);
        restart.Success.Should().BeTrue();
        var body = restart.Groups["body"].Value;
        body.IndexOf("_randomPick = null;", StringComparison.Ordinal).Should().BePositive();
        body.IndexOf("_randomPick = null;", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("CreateVariant(", StringComparison.Ordinal),
                "Preview must always play a fresh pick");
        source.Should().NotContain("CreateVariant(SplashStyle)", "every call site goes through ResolveStyle");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
