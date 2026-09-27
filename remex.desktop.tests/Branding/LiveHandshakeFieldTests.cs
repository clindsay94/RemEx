using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Remex.Branding;
using Xunit;

namespace Remex.Desktop.Tests.Branding;

/// <summary>
/// The Live Handshake field shader (RemEx-8g6n0): it compiles on the SkiaSharp this repo ships, it
/// declares the uniforms the variant sets, and it is byte-for-byte the shader Android compiles.
/// </summary>
public class LiveHandshakeFieldTests
{
    [Fact]
    public void TheEmbeddedShaderCompiles()
    {
        LiveHandshakeField.Effect.Should().NotBeNull($"the field must compile on SkiaSharp 3: {LiveHandshakeField.CompileErrors}");
        LiveHandshakeField.CompileErrors.Should().BeNull();
    }

    [Fact]
    public void ItDeclaresExactlyTheSpecUniforms()
    {
        var effect = LiveHandshakeField.Effect!;
        effect.Uniforms.Should().Equal(new[]
        {
            "uRes", "uTime", "uPx", "uCenter", "uBg0", "uBg1", "uPri", "uAcc", "uRings", "uRingCount",
            "uSpeed", "uIntro", "uPortal", "uZoom", "uStill", "uPar", "uAlpha",
        }, "the spec's Rendering section lists these, in this order");
        LiveHandshakeField.UniformNames.Should().Equal(effect.Uniforms);
        effect.Children.Should().BeEmpty("the field samples nothing; the portal is a transparent hole");
    }

    [Fact]
    public void TheEmbeddedCopyIsTheFileOnDisk()
    {
        var onDisk = File.ReadAllText(Path.Combine(RepoRoot(), "remex.branding", "Shaders", "live_handshake_field.sksl"));
        LiveHandshakeField.Source.Should().Be(onDisk);
    }

    [Fact]
    public void ThePcAndAndroidShadersAreByteIdentical()
    {
        var root = RepoRoot();
        var pc = File.ReadAllBytes(Path.Combine(root, "remex.branding", "Shaders", "live_handshake_field.sksl"));
        var android = File.ReadAllBytes(Path.Combine(root, "remex.android", "app", "src", "main", "res", "raw", "live_handshake_field.agsl"));

        pc.Should().Equal(android,
            "one shader, two platforms: edit remex.branding/Shaders/live_handshake_field.sksl and "
            + "remex.android/app/src/main/res/raw/live_handshake_field.agsl together or not at all");
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
    {
        var dir = Path.GetDirectoryName(thisSourceFile)!;
        while (!File.Exists(Path.Combine(dir, "Remex.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
