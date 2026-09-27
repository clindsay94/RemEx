using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// The Live Handshake backdrop field (RemEx-8g6n0): <c>Shaders/live_handshake_field.sksl</c>, embedded
/// in this assembly and compiled once per process. The source is byte-identical with the Android
/// <c>live_handshake_field.agsl</c> (a parity test compares the two files); never edit one alone.
/// </summary>
public static class LiveHandshakeField
{
    private const string ResourceName = "Remex.Branding.Shaders.live_handshake_field.sksl";

    /// <summary>Every uniform the shader declares, in declaration order (the spec's list).</summary>
    public static IReadOnlyList<string> UniformNames { get; } = new[]
    {
        "uRes", "uTime", "uPx", "uCenter", "uBg0", "uBg1", "uPri", "uAcc", "uRings", "uRingCount",
        "uSpeed", "uIntro", "uPortal", "uZoom", "uStill", "uPar", "uAlpha",
    };

    /// <summary>How many rings the shader reads (float4 each).</summary>
    public const int MaxRings = 10;

    private static readonly Lazy<string> SourceLazy = new(LoadSource);
    private static readonly object CompileGate = new();
    private static volatile bool _compiled;
    private static SKRuntimeEffect? _effect;
    private static string? _errors;

    /// <summary>The shader source as embedded.</summary>
    public static string Source => SourceLazy.Value;

    /// <summary>
    /// The compiled effect, or null when compilation failed (<see cref="CompileErrors"/> says why).
    /// Compiled on first use and cached for the process; the splash then only builds a per-frame
    /// shader from it. A null here degrades the splash to its gradient backdrop, never a crash.
    /// </summary>
    public static SKRuntimeEffect? Effect
    {
        get
        {
            if (_compiled) return _effect;
            lock (CompileGate)
            {
                if (_compiled) return _effect;
                try
                {
                    _effect = SKRuntimeEffect.CreateShader(Source, out var errors);
                    _errors = string.IsNullOrEmpty(errors) ? null : errors;
                }
                catch (Exception ex)
                {
                    _effect = null;
                    _errors = ex.Message;
                }
                _compiled = true;
                return _effect;
            }
        }
    }

    /// <summary>
    /// Starts compiling the effect on a thread-pool thread if it has not been compiled yet, so the
    /// splash's first frame does not pay for the SkSL compile on the render path. Idempotent and
    /// fire-and-forget: <see cref="Effect"/> takes the same lock, so a frame that arrives mid-compile
    /// simply waits for it, and a failure is captured in <see cref="CompileErrors"/>, never thrown.
    /// </summary>
    public static void WarmUp()
    {
        if (_compiled) return;
        ThreadPool.QueueUserWorkItem(static _ => { _ = Effect; });
    }

    /// <summary>The compiler's message when <see cref="Effect"/> is null.</summary>
    public static string? CompileErrors
    {
        get
        {
            _ = Effect;
            return _errors;
        }
    }

    private static string LoadSource()
    {
        using var stream = typeof(LiveHandshakeField).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded shader missing: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
