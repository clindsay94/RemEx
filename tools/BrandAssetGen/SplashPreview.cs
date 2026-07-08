using Remex.Branding;
using SkiaSharp;

namespace Remex.Tools.BrandAssetGen;

/// <summary>
/// Renders a single splash-variant frame to a PNG for visual verification — no Avalonia app needed
/// (the variants are pure SkiaSharp). Usage: BrandAssetGen splash &lt;style&gt; &lt;timeMs&gt; &lt;out.png&gt; [w] [h]
/// </summary>
internal static class SplashPreview
{
    public static int Run(string[] args)
    {
        try
        {
            string style = args.Length > 1 ? args[1] : "RemexCommand";
            float t = args.Length > 2 && float.TryParse(args[2], out float ms) ? ms / 1000f : 1.5f;
            string outPath = args.Length > 3 ? args[3] : "splash.png";
            int w = args.Length > 4 && int.TryParse(args[4], out int pw) ? pw : 1200;
            int h = args.Length > 5 && int.TryParse(args[5], out int ph) ? ph : 800;

            LoadFont();
            ISplashVariant variant = style switch
            {
                "CosmicZoom" => new CosmicZoomVariant(),
                "Pong" => new PongVariant(),
                _ => new RemexCommandVariant(),
            };

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            // Step from 0 to t so mutable state (particles, phases) is correct; the opaque backdrop each
            // frame means the final frame is clean regardless of prior overdraw.
            const float dt = 1f / 60f;
            float acc = 0f;
            if (t <= 0f) variant.Render(canvas, w, h, 0f, 0f);
            while (acc < t)
            {
                float step = MathF.Min(dt, t - acc);
                acc += step;
                variant.Render(canvas, w, h, acc, step);
            }

            canvas.Flush();
            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(outPath, data.ToArray());
            Console.WriteLine($"wrote {outPath}  ({style} @ {t:0.00}s, {w}x{h})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: splash preview failed: {ex.Message}");
            return 1;
        }
    }

    private static void LoadFont()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remex.sln"))) dir = dir.Parent;
        if (dir is null) return;
        string ttf = Path.Combine(dir.FullName, "remex.desktop", "Assets", "Fonts", "victor_mono_bold.ttf");
        if (File.Exists(ttf))
        {
            using var fs = File.OpenRead(ttf);
            SplashBrand.LoadTypeface(fs);
        }
    }
}
