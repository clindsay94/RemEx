using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Branding;
using SkiaSharp;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

/// <summary>
/// The film splashes' field shader (RemEx-pp4cm.11): it compiles on the SkiaSharp this repo ships, it
/// declares the uniforms <see cref="SplashFilmField.Draw"/> sets, it is byte-for-byte the shader
/// Android compiles, and every film actually paints it (and its still frame under reduced motion).
/// </summary>
public class SplashFilmFieldTests
{
    [Fact]
    public void TheEmbeddedShaderCompiles()
    {
        SplashFilmField.Effect.Should().NotBeNull($"the film field must compile on SkiaSharp 3: {SplashFilmField.CompileErrors}");
        SplashFilmField.CompileErrors.Should().BeNull();
    }

    [Fact]
    public void ItDeclaresExactlyTheUniformsTheDrawSets()
    {
        var effect = SplashFilmField.Effect!;
        effect.Uniforms.Should().Equal(SplashFilmField.UniformNames,
            "Draw sets these by name; a uniform the compiler strips or renames would throw at runtime");
        effect.Children.Should().BeEmpty("the field samples nothing");
    }

    [Fact]
    public void TheEmbeddedCopyIsTheFileOnDisk()
    {
        var onDisk = File.ReadAllText(Path.Combine(RepoRoot(), "remex.branding", "Shaders", "splash_film_field.sksl"));
        SplashFilmField.Source.Should().Be(onDisk);
    }

    [Fact]
    public void ThePcAndAndroidShadersAreByteIdentical()
    {
        var root = RepoRoot();
        var pc = File.ReadAllBytes(Path.Combine(root, "remex.branding", "Shaders", "splash_film_field.sksl"));
        var android = File.ReadAllBytes(Path.Combine(root, "remex.android", "app", "src", "main", "res", "raw", "splash_film_field.agsl"));

        pc.Should().Equal(android,
            "one shader, two platforms: edit remex.branding/Shaders/splash_film_field.sksl and "
            + "remex.android/app/src/main/res/raw/splash_film_field.agsl together or not at all");
    }

    [Theory]
    [InlineData(SplashFilmStyle.Command)]
    [InlineData(SplashFilmStyle.Cosmic)]
    [InlineData(SplashFilmStyle.Pong)]
    public void EachWorldPaintsMoreThanTheFlatGradient(SplashFilmStyle style)
    {
        // Anti-vacuity: the same frame with the field off is the plain gradient. The field must add
        // visible structure (lines, stars, a court), measured as many more distinct colours.
        int withField = DistinctColours(c => SplashFilmField.Draw(c, 320, 200, style, 1.0f, 0.8f,
            new SKPoint(160, 100), 0.6f, FilmBeat.None, still: false, enabled: true));
        int gradient = DistinctColours(c => SplashFilmField.Draw(c, 320, 200, style, 1.0f, 0.8f,
            new SKPoint(160, 100), 0.6f, FilmBeat.None, still: false, enabled: false));

        withField.Should().BeGreaterThan(gradient * 2, $"{style} should draw its world, not just the backdrop");
    }

    [Fact]
    public void ABeatLightsTheFieldWhereItsCrestIs()
    {
        // A beat at the centre at t = 0, seen 0.1 s later: the crest is a ring around the centre.
        var beat = new FilmBeat(160, 100, 0f, 1.2f);
        using var lit = Render(c => SplashFilmField.Draw(c, 320, 200, SplashFilmStyle.Pong, 0.1f, 0.8f,
            new SKPoint(160, 100), 0.5f, beat, still: false, enabled: true));
        using var dark = Render(c => SplashFilmField.Draw(c, 320, 200, SplashFilmStyle.Pong, 0.1f, 0.8f,
            new SKPoint(160, 100), 0.5f, FilmBeat.None, still: false, enabled: true));

        double litSum = 0, darkSum = 0;
        for (int x = 0; x < 320; x += 4)
        for (int y = 0; y < 200; y += 4)
        {
            litSum += Luma(lit.GetPixel(x, y));
            darkSum += Luma(dark.GetPixel(x, y));
        }
        litSum.Should().BeGreaterThan(darkSum * 1.02, "the beat's crest and wake add accent light");
    }

    [Fact]
    public void ReducedMotionTurnsEveryFilmIntoAShortStill()
    {
        IFieldSplashVariant[] films = { new RemexCommandVariant(), new CosmicZoomVariant(), new PongVariant() };
        foreach (var film in films)
        {
            float full = film.Duration;
            film.ReducedMotion = true;
            film.Duration.Should().Be(SplashFilmField.StillDuration, $"{film.GetType().Name} shows its still, held then faded");
            film.Duration.Should().BeLessThan(full);

            // Deterministic: the same instant twice gives the same pixels (nothing travels).
            film.FieldEnabled = true;
            var a = BrandRasterizer.RenderSplashFramePng(film, 240, 160, 0.3f);
            var b = BrandRasterizer.RenderSplashFramePng(film, 240, 160, 0.9f);
            a.Should().Equal(b, $"{film.GetType().Name}'s still frame must not move while it holds");
        }
    }

    private static int DistinctColours(Action<SKCanvas> draw)
    {
        using var bmp = Render(draw);
        var seen = new System.Collections.Generic.HashSet<uint>();
        for (int x = 0; x < bmp.Width; x++)
        for (int y = 0; y < bmp.Height; y++)
            seen.Add((uint)bmp.GetPixel(x, y));
        return seen.Count;
    }

    private static SKBitmap Render(Action<SKCanvas> draw)
    {
        var bmp = new SKBitmap(new SKImageInfo(320, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        draw(canvas);
        canvas.Flush();
        return bmp;
    }

    private static double Luma(SKColor c) => 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
