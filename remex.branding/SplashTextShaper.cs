using System.Globalization;
using System.Text;
using HarfBuzzSharp;
using SkiaSharp;

namespace Remex.Branding;

/// <summary>
/// One line of splash text, shaped and ready to draw: an <see cref="SKTextBlob"/> plus its advance
/// width. <see cref="Glyphs"/> is every glyph id placed, for tests (glyph 0 is "missing").
/// </summary>
public sealed class ShapedLine : IDisposable
{
    internal ShapedLine(SKTextBlob? blob, float width, ushort[] glyphs)
    {
        Blob = blob;
        Width = width;
        Glyphs = glyphs;
    }

    public SKTextBlob? Blob { get; }
    public float Width { get; }
    public IReadOnlyList<ushort> Glyphs { get; }

    /// <summary>Draws the line with its left edge at <paramref name="x"/> and baseline at <paramref name="y"/>.</summary>
    public void Draw(SKCanvas canvas, float x, float y, SKPaint paint)
    {
        if (Blob is not null) canvas.DrawText(Blob, x, y, paint);
    }

    public void Dispose() => Blob?.Dispose();
}

/// <summary>
/// Shapes splash text properly (RemEx-8g6n0.2 review): <c>SKCanvas.DrawText</c> with one font has no
/// glyph fallback and no shaping, so Devanagari (the <c>hi</c> status line, a Hindi nickname) drew as
/// missing-glyph boxes and would have been mis-ordered anyway. This splits the text into runs by
/// typeface — the brand face where it has the glyph, otherwise whatever system face
/// <see cref="SKFontManager.MatchCharacter(string, SKFontStyle, string[], int)"/> offers — and shapes
/// each run with HarfBuzz (the same engine Avalonia uses for the rest of the app), then builds one
/// positioned <see cref="SKTextBlob"/>. What SkiaSharp.HarfBuzz's SKShaper does, without adding a package.
/// </summary>
/// <remarks>
/// Combining marks, joiners and variation selectors stay in the run of the character they attach to,
/// so a consonant and its vowel sign are never shaped by two different fonts. Not thread-safe: one
/// shaper per render thread (the splash owns one).
/// </remarks>
public sealed class SplashTextShaper : IDisposable
{
    /// <summary>HarfBuzz works in integer units; shape at this scale and divide down (as SKShaper does).</summary>
    private const int HbScale = 512;

    private readonly Dictionary<SKTypeface, HbFace> _hb = new();
    private readonly Dictionary<int, SKTypeface?> _fallback = new();

    /// <summary>Shapes <paramref name="text"/> at <paramref name="size"/> with <paramref name="primary"/> first.</summary>
    public ShapedLine Shape(string text, SKTypeface primary, float size)
    {
        if (string.IsNullOrEmpty(text)) return new ShapedLine(null, 0f, Array.Empty<ushort>());

        var runs = SplitRuns(text, primary);
        using var builder = new SKTextBlobBuilder();
        var allGlyphs = new List<ushort>(text.Length);
        float pen = 0f;
        float scale = size / HbScale;

        foreach (var (start, length, face) in runs)
        {
            var hb = HarfBuzzFor(face);
            using var buffer = new HarfBuzzSharp.Buffer();
            buffer.AddUtf16(text.AsSpan(), start, length);
            buffer.GuessSegmentProperties();
            hb.Font.Shape(buffer);

            var infos = buffer.GetGlyphInfoSpan();
            var positions = buffer.GetGlyphPositionSpan();
            int count = infos.Length;
            if (count == 0) continue;

            using var font = new SKFont(face, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
            var run = builder.AllocatePositionedRun(font, count);
            var glyphs = run.Glyphs;
            var points = run.Positions;
            for (int i = 0; i < count; i++)
            {
                glyphs[i] = (ushort)infos[i].Codepoint;
                points[i] = new SKPoint(pen + positions[i].XOffset * scale, -positions[i].YOffset * scale);
                pen += positions[i].XAdvance * scale;
                allGlyphs.Add(glyphs[i]);
            }
        }

        return new ShapedLine(builder.Build(), pen, allGlyphs.ToArray());
    }

    private List<(int Start, int Length, SKTypeface Face)> SplitRuns(string text, SKTypeface primary)
    {
        var runs = new List<(int, int, SKTypeface)>();
        SKTypeface? current = null;
        int runStart = 0;
        int i = 0;
        while (i < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out int consumed);
            if (status != System.Buffers.OperationStatus.Done) { rune = Rune.ReplacementChar; consumed = 1; }
            int cp = rune.Value;

            SKTypeface face;
            if (current is not null && Attaches(rune)) face = current;
            else if (primary.ContainsGlyph(cp)) face = primary;
            else if (current is not null && current.ContainsGlyph(cp)) face = current;
            else face = Fallback(cp, primary) ?? primary;

            if (!ReferenceEquals(face, current))
            {
                if (current is not null) runs.Add((runStart, i - runStart, current));
                current = face;
                runStart = i;
            }
            i += consumed;
        }
        if (current is not null) runs.Add((runStart, text.Length - runStart, current));
        return runs;
    }

    /// <summary>Marks, joiners and selectors belong to the preceding character's run.</summary>
    private static bool Attaches(Rune rune)
    {
        int cp = rune.Value;
        if (cp is 0x200C or 0x200D) return true;                 // ZWNJ / ZWJ
        if (cp is >= 0xFE00 and <= 0xFE0F) return true;          // variation selectors
        var cat = Rune.GetUnicodeCategory(rune);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private SKTypeface? Fallback(int codepoint, SKTypeface primary)
    {
        if (_fallback.TryGetValue(codepoint, out var cached)) return cached;
        SKTypeface? face = null;
        try
        {
            face = SKFontManager.Default.MatchCharacter(primary.FamilyName, primary.FontStyle, null, codepoint);
            // Reuse an equivalent face already in use, so one script does not split into many runs.
            if (face is not null)
            {
                foreach (var known in _hb.Keys)
                {
                    if (known.FamilyName == face.FamilyName && known.FontStyle.Weight == face.FontStyle.Weight
                        && known.FontStyle.Slant == face.FontStyle.Slant)
                    {
                        face = known;
                        break;
                    }
                }
                foreach (var known in _fallback.Values)
                {
                    if (known is not null && known.FamilyName == face.FamilyName
                        && known.FontStyle.Weight == face.FontStyle.Weight && known.FontStyle.Slant == face.FontStyle.Slant)
                    {
                        face = known;
                        break;
                    }
                }
            }
        }
        catch
        {
            face = null; // no font manager (headless edge case): draw with the primary, boxes and all
        }
        _fallback[codepoint] = face;
        return face;
    }

    private HbFace HarfBuzzFor(SKTypeface face)
    {
        if (_hb.TryGetValue(face, out var hb)) return hb;
        hb = HbFace.Create(face);
        _hb[face] = hb;
        return hb;
    }

    public void Dispose()
    {
        foreach (var hb in _hb.Values) hb.Dispose();
        _hb.Clear();
        _fallback.Clear();
    }

    /// <summary>A HarfBuzz font over the same font file the SKTypeface draws from.</summary>
    private sealed class HbFace : IDisposable
    {
        private readonly Blob _blob;
        private readonly Face _face;
        public HarfBuzzSharp.Font Font { get; }

        private HbFace(Blob blob, Face face, HarfBuzzSharp.Font font)
        {
            _blob = blob;
            _face = face;
            Font = font;
        }

        public static HbFace Create(SKTypeface typeface)
        {
            byte[] data;
            int ttcIndex;
            using (var stream = typeface.OpenStream(out ttcIndex))
            {
                data = new byte[stream?.Length ?? 0];
                if (stream is not null && data.Length > 0) stream.Read(data, data.Length);
            }
            var blob = Blob.FromStream(new MemoryStream(data, writable: false));
            var face = new Face(blob, ttcIndex) { Index = ttcIndex, UnitsPerEm = typeface.UnitsPerEm };
            var font = new HarfBuzzSharp.Font(face);
            font.SetScale(HbScale, HbScale);
            font.SetFunctionsOpenType();
            return new HbFace(blob, face, font);
        }

        public void Dispose()
        {
            Font.Dispose();
            _face.Dispose();
            _blob.Dispose();
        }
    }
}
