using Remex.Branding;
using SkiaSharp;

namespace Remex.Tools.BrandAssetGen;

/// <summary>
/// Renders a single splash-variant frame to a PNG for visual verification — no Avalonia app needed
/// (the variants are pure SkiaSharp). Usage: BrandAssetGen splash &lt;style&gt; &lt;timeMs&gt; &lt;out.png&gt; [w] [h] [reduced]
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
            if (style == "LiveHandshake")
            {
                // Deterministic in t, so one frame is rendered directly (the field shader runs on the
                // CPU here, which is far too slow to step). The lab's "pc" scenario: three phones known,
                // the S26 links at 0.86 s, the listener is up at 0.40 s.
                using var live = new LiveHandshakeVariant();
                live.Update(new HandshakeSnapshot(
                    new[]
                    {
                        new HandshakePeer("Pixel 9 Pro", HandshakeDeviceKind.Phone, null),
                        new HandshakePeer("Galaxy S26 Ultra", HandshakeDeviceKind.Phone, 0.86f),
                        new HandshakePeer("Galaxy Tab S10", HandshakeDeviceKind.Tablet, null),
                    },
                    TargetIndex: 1, TargetAt: 0.86f, ReadyAt: 0.55f, ListeningAt: 0.40f, ListeningPort: 5005,
                    FailedAt: null));
                File.WriteAllBytes(outPath, BrandRasterizer.RenderSplashFramePng(live, w, h, t));
                Console.WriteLine($"wrote {outPath}  ({style} @ {t:0.00}s, {w}x{h})");
                return 0;
            }

            IFieldSplashVariant variant = style switch
            {
                "CosmicZoom" => new CosmicZoomVariant(),
                "Pong" => new PongVariant(),
                _ => new RemexCommandVariant(),
            };
            // Optional 7th argument "reduced": the film's reduced-motion still frame (RemEx-pp4cm.11).
            variant.ReducedMotion = args.Length > 6 && args[6] == "reduced";

            using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            // Step from 0 to t so mutable state (particles, phases) is correct; the opaque backdrop each
            // frame means the final frame is clean regardless of prior overdraw. The field shader runs on
            // the CPU here, so only the frame that is kept pays for it.
            const float dt = 1f / 60f;
            float acc = 0f;
            variant.FieldEnabled = t <= 0f;
            if (t <= 0f) variant.Render(canvas, w, h, 0f, 0f);
            while (acc < t)
            {
                float step = MathF.Min(dt, t - acc);
                acc += step;
                variant.FieldEnabled = acc >= t;
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
