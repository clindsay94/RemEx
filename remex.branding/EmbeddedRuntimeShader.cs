using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// An SkSL shader embedded in this assembly, compiled once per process on first use (or ahead of it
/// through <see cref="WarmUp"/>). Shared by the splash fields (<see cref="LiveHandshakeField"/>,
/// <see cref="SplashFilmField"/>): a failed compile is captured in <see cref="CompileErrors"/> and
/// leaves <see cref="Effect"/> null, so a splash degrades to its gradient backdrop, never a crash.
/// </summary>
internal sealed class EmbeddedRuntimeShader
{
    private readonly string _resourceName;
    private readonly Lazy<string> _source;
    private readonly object _compileGate = new();
    private volatile bool _compiled;
    private SKRuntimeEffect? _effect;
    private string? _errors;

    public EmbeddedRuntimeShader(string resourceName)
    {
        _resourceName = resourceName;
        _source = new Lazy<string>(LoadSource);
    }

    /// <summary>The shader source as embedded.</summary>
    public string Source => _source.Value;

    /// <summary>The compiled effect, or null when compilation failed.</summary>
    public SKRuntimeEffect? Effect
    {
        get
        {
            if (_compiled) return _effect;
            lock (_compileGate)
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
    /// Starts compiling on a thread-pool thread if that has not happened yet, so a splash's first frame
    /// does not pay for the SkSL compile on the render path. Idempotent and fire-and-forget:
    /// <see cref="Effect"/> takes the same lock, so a frame that arrives mid-compile waits for it.
    /// </summary>
    public void WarmUp()
    {
        if (_compiled) return;
        ThreadPool.QueueUserWorkItem(static state => { _ = ((EmbeddedRuntimeShader)state!).Effect; }, this);
    }

    /// <summary>The compiler's message when <see cref="Effect"/> is null.</summary>
    public string? CompileErrors
    {
        get
        {
            _ = Effect;
            return _errors;
        }
    }

    private string LoadSource()
    {
        using var stream = typeof(EmbeddedRuntimeShader).Assembly.GetManifestResourceStream(_resourceName)
            ?? throw new InvalidOperationException($"Embedded shader missing: {_resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
