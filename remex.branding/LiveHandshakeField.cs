using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// The Live Handshake backdrop field (RemEx-8g6n0): <c>Shaders/live_handshake_field.sksl</c>, embedded
/// in this assembly and compiled once per process. The source is byte-identical with the Android
/// <c>live_handshake_field.agsl</c> (a parity test compares the two files); never edit one alone.
/// </summary>
public static class LiveHandshakeField
{
    private static readonly EmbeddedRuntimeShader Shader = new("Remex.Branding.Shaders.live_handshake_field.sksl");

    /// <summary>Every uniform the shader declares, in declaration order (the spec's list).</summary>
    public static IReadOnlyList<string> UniformNames { get; } = new[]
    {
        "uRes", "uTime", "uPx", "uCenter", "uBg0", "uBg1", "uPri", "uAcc", "uRings", "uRingCount",
        "uSpeed", "uIntro", "uPortal", "uZoom", "uStill", "uPar", "uAlpha",
    };

    /// <summary>How many rings the shader reads (float4 each).</summary>
    public const int MaxRings = 10;

    /// <summary>The shader source as embedded.</summary>
    public static string Source => Shader.Source;

    /// <summary>
    /// The compiled effect, or null when compilation failed (<see cref="CompileErrors"/> says why).
    /// Compiled on first use and cached for the process; the splash then only builds a per-frame
    /// shader from it. A null here degrades the splash to its gradient backdrop, never a crash.
    /// </summary>
    public static SKRuntimeEffect? Effect => Shader.Effect;

    /// <summary>
    /// Starts compiling the effect on a thread-pool thread if it has not been compiled yet, so the
    /// splash's first frame does not pay for the SkSL compile on the render path. Idempotent and
    /// fire-and-forget; a failure is captured in <see cref="CompileErrors"/>, never thrown.
    /// </summary>
    public static void WarmUp() => Shader.WarmUp();

    /// <summary>The compiler's message when <see cref="Effect"/> is null.</summary>
    public static string? CompileErrors => Shader.CompileErrors;
}
