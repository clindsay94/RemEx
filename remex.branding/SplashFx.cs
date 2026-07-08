using SkiaSharp;

namespace Remex.Branding;

/// <summary>Starfield particle (CosmicZoom) / rising ember (RemexCommand). Positions are normalized 0..1.</summary>
public sealed class Particle
{
    public float X, Y, Vx, Vy, Lifetime, MaxLifetime, Alpha;
}

/// <summary>
/// RemexCommand ambient floating shape. Morphs between two rounded polygons (radius-blend model:
/// rounding 0 = polygon, 1 = circle) — the faithful SkiaSharp equivalent of Android's
/// androidx.graphics.shapes Morph for this low-alpha background layer.
/// </summary>
public sealed class FloatingShape
{
    public float X, Y, Size, Vx, Vy, Rotation, RotationSpeed, Alpha, MorphProgress, MorphSpeed;
    public int SidesA = 4, SidesB = 6;
    public float RoundA = 0.2f, RoundB = 0.5f;
    public SKColor Color;
}

/// <summary>Shared splash background FX, ported from SplashBackgroundFx.kt.</summary>
public static class SplashFx
{
    private static byte A(float o) => (byte)Math.Clamp((int)(o * 255f + 0.5f), 0, 255);

    /// <summary>Cosmic starfield: streaking white points radiating from center. Positions normalized.</summary>
    public static void DrawStarfield(SKCanvas canvas, float width, float height, IReadOnlyList<Particle> particles)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        foreach (var p in particles)
        {
            float sx = p.X * width, sy = p.Y * height;
            float dx = p.X - 0.5f, dy = p.Y - 0.5f;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float radius = 0.5f + dist * 3.5f;
            paint.Color = SKColors.White.WithAlpha(A(p.Alpha));
            canvas.DrawCircle(sx, sy, radius, paint);
        }
    }

    /// <summary>
    /// RemexCommand ambient layer: circuit traces, 7-node topology, floating morph shapes, radial grid.
    /// Colors supplied by the caller (surface, surfaceVariant) exactly as the Android monolith did.
    /// </summary>
    public static void DrawAmbientFx(SKCanvas canvas, float width, float height,
        IReadOnlyList<FloatingShape> floatingShapes, SKColor surface, SKColor surfaceVariant)
    {
        float cx = width / 2f, cy = height / 2f;

        // Circuit board traces (seeded Random(55) — matches Android's java.util.Random(55L) sequence intent).
        var traceColor = surface.WithAlpha(A(0.06f));
        var traceRng = new DeterministicRandom(55);
        using (var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = traceColor })
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = traceColor })
        {
            for (int i = 0; i < 20; i++)
            {
                float sx = traceRng.NextFloat() * width;
                float sy = traceRng.NextFloat() * height;
                float seg1 = 30f + traceRng.NextFloat() * 80f;
                float seg2 = 20f + traceRng.NextFloat() * 60f;
                bool horiz = traceRng.NextBool();
                float d1 = traceRng.NextBool() ? 1f : -1f;
                float d2 = traceRng.NextBool() ? 1f : -1f;
                float tx, ty, ex, ey;
                if (horiz) { tx = sx + seg1 * d1; ty = sy; ex = tx; ey = ty + seg2 * d2; }
                else { tx = sx; ty = sy + seg1 * d1; ex = tx + seg2 * d2; ey = ty; }
                using var p = new SKPath();
                p.MoveTo(sx, sy); p.LineTo(tx, ty); p.LineTo(ex, ey);
                canvas.DrawPath(p, stroke);
                canvas.DrawCircle(tx, ty, 4f, fill);
            }
        }

        // Network topology nodes (seeded Random(123)).
        var nodeColor = surfaceVariant.WithAlpha(A(0.07f));
        var nodeRng = new DeterministicRandom(123);
        var nodes = new SKPoint[7];
        for (int i = 0; i < 7; i++) nodes[i] = new SKPoint(nodeRng.NextFloat() * width, nodeRng.NextFloat() * height);
        using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = nodeColor })
        using (var dot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = nodeColor })
        {
            for (int i = 0; i < nodes.Length; i++)
                for (int j = i + 1; j < nodes.Length; j++)
                    if (Hypot(nodes[i].X - nodes[j].X, nodes[i].Y - nodes[j].Y) < width * 0.35f)
                        canvas.DrawLine(nodes[i], nodes[j], line);
            foreach (var n in nodes) canvas.DrawCircle(n.X, n.Y, 3f, dot);
        }

        // Floating morph shapes.
        using (var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            foreach (var shape in floatingShapes)
            {
                float shx = shape.X * width, shy = shape.Y * height;
                float r = shape.Size * MathF.Min(width, height);
                using var path = MorphedPolygon(shape, r, 48);
                stroke.Color = shape.Color.WithAlpha(A(shape.Alpha));
                fill.Color = shape.Color.WithAlpha(A(shape.Alpha * 0.3f));
                canvas.Save();
                canvas.Translate(shx, shy);
                canvas.RotateDegrees(shape.Rotation);
                canvas.DrawPath(path, fill);
                canvas.DrawPath(path, stroke);
                canvas.Restore();
            }
        }

        // Radial grid.
        var radColor = surfaceVariant.WithAlpha(A(0.03f));
        float maxRad = MathF.Min(width, height) * 0.45f;
        using (var grid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = radColor })
        {
            foreach (float rf in new[] { 0.25f, 0.45f, 0.65f, 0.85f })
                canvas.DrawCircle(cx, cy, rf * maxRad, grid);
            for (int i = 0; i < 8; i++)
            {
                float a = i * (MathF.PI / 4f);
                canvas.DrawLine(cx, cy, cx + maxRad * MathF.Cos(a), cy + maxRad * MathF.Sin(a), grid);
            }
        }
    }

    /// <summary>Build a morphed rounded-polygon path at the given radius (blend of shape A and B by MorphProgress).</summary>
    private static SKPath MorphedPolygon(FloatingShape s, float radius, int samples)
    {
        var path = new SKPath();
        for (int i = 0; i <= samples; i++)
        {
            float ang = i / (float)samples * MathF.PI * 2f;
            float rA = RoundedRadius(ang, s.SidesA, s.RoundA);
            float rB = RoundedRadius(ang, s.SidesB, s.RoundB);
            float rr = (rA + (rB - rA) * Math.Clamp(s.MorphProgress, 0f, 1f)) * radius;
            float x = rr * MathF.Cos(ang), y = rr * MathF.Sin(ang);
            if (i == 0) path.MoveTo(x, y); else path.LineTo(x, y);
        }
        path.Close();
        return path;
    }

    /// <summary>Radius of a rounded regular polygon at a given angle (rounding 0 = polygon edge, 1 = circle).</summary>
    private static float RoundedRadius(float angle, int sides, float rounding)
    {
        float seg = MathF.PI * 2f / sides;
        float a = angle % seg; if (a < 0) a += seg;
        float edgeR = MathF.Cos(seg / 2f) / MathF.Cos(a - seg / 2f);
        return edgeR + (1f - edgeR) * Math.Clamp(rounding, 0f, 1f);
    }

    private static float Hypot(float a, float b) => MathF.Sqrt(a * a + b * b);
}

/// <summary>
/// Small deterministic LCG so seeded ambient FX (traces/nodes) are byte-stable across runs and
/// platforms — Date/Random aren't used, matching the "reproducible splash" intent.
/// </summary>
internal sealed class DeterministicRandom
{
    private ulong _state;
    public DeterministicRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    private uint Next()
    {
        // xorshift64*
        _state ^= _state >> 12; _state ^= _state << 25; _state ^= _state >> 27;
        return (uint)((_state * 0x2545F4914F6CDD1DUL) >> 32);
    }

    public float NextFloat() => (Next() & 0xFFFFFF) / (float)0x1000000; // [0,1)
    public bool NextBool() => (Next() & 1) == 1;
}
