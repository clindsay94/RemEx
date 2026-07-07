using Remex.Branding;

// Regenerates every PC brand raster from the shared geometry in remex.branding.
// Cross-platform (SkiaSharp only). Idempotent: same input => byte-stable output.
try
{
    string repoRoot = FindRepoRoot();
    string assets = Path.Combine(repoRoot, "remex.desktop", "Assets");
    string windows = Path.Combine(assets, "windows");
    if (!Directory.Exists(assets))
        throw new DirectoryNotFoundException($"Could not find {assets}. Run from within the RemEx repo.");

    int written = 0;

    // Window/tray icon.
    WritePng(Path.Combine(assets, "icon.png"), 512, 512, ref written);

    // Exe icon (multi-res).
    int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };
    var frames = icoSizes.Select(n => (n, BrandRasterizer.RenderPng(n, n))).ToList();
    AtomicWrite(Path.Combine(assets, "icon.ico"), IcoWriter.Build(frames));
    written++;
    Console.WriteLine($"  wrote icon.ico ({icoSizes.Length} frames)");

    // MSIX packaging set — reproduce every existing file at its correct size.
    int skipped = 0;
    if (Directory.Exists(windows))
    {
        foreach (string path in Directory.EnumerateFiles(windows, "*.png").OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            var dims = MsixAssetPlan.Dimensions(file);
            if (dims is null) { Console.WriteLine($"  SKIP (unknown) {file}"); skipped++; continue; }
            WritePng(path, dims.Value.Width, dims.Value.Height, ref written);
        }
    }

    Console.WriteLine($"\nBrand assets regenerated: {written} file(s) written, {skipped} skipped.");
    if (skipped > 0)
    {
        Console.Error.WriteLine($"WARNING: {skipped} file(s) in Assets/windows had unrecognized names and were left unchanged.");
        return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: brand asset generation failed: {ex.Message}");
    return 1;
}

static void WritePng(string path, int w, int h, ref int written)
{
    byte[] png = BrandRasterizer.RenderPng(w, h);
    if (png.Length == 0) throw new InvalidOperationException($"Empty PNG produced for {Path.GetFileName(path)} ({w}x{h}).");
    AtomicWrite(path, png);
    written++;
    Console.WriteLine($"  wrote {Path.GetFileName(path)} ({w}x{h})");
}

static void AtomicWrite(string path, byte[] bytes)
{
    string tmp = path + ".tmp";
    File.WriteAllBytes(tmp, bytes);
    File.Move(tmp, path, overwrite: true);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Remex.sln"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not locate Remex.sln walking up from the tool directory.");
}
