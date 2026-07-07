namespace Remex.Branding;

/// <summary>
/// The single source of truth for the RemEx brand mark. Palette (ARGB) and vector path
/// strings are copied verbatim from the shipped Android adaptive launcher icon
/// (res/drawable/ic_launcher_foreground_vector.xml + ic_launcher_background.xml) so the PC
/// mark is identical to Android, not an approximation. Zero dependencies by design — the
/// SkiaSharp renderer, the Avalonia About control, and the asset generator all read from here.
/// </summary>
public static class RemexBrandData
{
    // Palette (ARGB) — fixed brand colors, never theme-adaptive.
    public const uint BackdropStartArgb = 0xFF20242F;
    public const uint BackdropEndArgb   = 0xFF39404F;
    public const uint WindowFillArgb    = 0xFF0C0E13;
    public const uint WindowStrokeArgb  = 0xFF7C88A0;
    public const uint AmberArgb         = 0xFFFFB63D;
    public const uint SlateLoArgb       = 0xFF6E7B94;
    public const uint SlateHiArgb       = 0xFF333B48;
    public const uint OffWhiteArgb      = 0xFFF4F5F9;

    // Geometry (108-unit viewport, matching the vector drawable).
    public const float Viewport           = 108f;
    public const float GroupScale         = 0.8f;   // <group scaleX/Y="0.8">
    public const float GroupPivot         = 54f;    // pivotX/Y="54"
    public const float GradStart          = 6f;     // gradient startX/startY
    public const float GradEnd            = 102f;   // gradient endX/endY
    public const float WindowStrokeAlpha  = 0.55f;
    public const float WindowStrokeWidth  = 1.4f;
    public const float ChevronStrokeWidth = 4.8f;

    // Foreground path data — verbatim from ic_launcher_foreground_vector.xml.
    public const string WindowPath  = "M33,26 L75,26 A13,13 0 0 1 88,39 L88,69 A13,13 0 0 1 75,82 L33,82 A13,13 0 0 1 20,69 L20,39 A13,13 0 0 1 33,26 Z";
    public const string Dot1Path    = "M27.3,36 a2.2,2.2 0 1,0 4.4,0 a2.2,2.2 0 1,0 -4.4,0 Z";
    public const string Dot2Path    = "M34.3,36 a2.2,2.2 0 1,0 4.4,0 a2.2,2.2 0 1,0 -4.4,0 Z";
    public const string Dot3Path    = "M41.3,36 a2.2,2.2 0 1,0 4.4,0 a2.2,2.2 0 1,0 -4.4,0 Z";
    public const string ChevronPath = "M30,50 L39,61 L30,72";
    public const string RStemPath   = "M45,47 h7 v25 h-7 Z";
    public const string RBowlPath   = "M52,47 H60 A8,8 0 0 1 60,63 H52 Z";
    public const string RLegPath    = "M53,58.5 L61,58.5 L71.5,73 L63,73 Z";
    public const string RHolePath   = "M53.5,53 H58.8 A2.8,2.8 0 0 1 58.8,58.6 H53.5 Z";
    public const string CursorPath  = "M73,50 h6 v22 h-6 Z";
}
