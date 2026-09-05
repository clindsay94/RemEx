using System.Text.Json.Serialization;

namespace Remex.Core.Models;

/// <summary>
/// Persisted state of a single card within a saved dashboard layout.
/// </summary>
public record CardState
{
    /// <summary>Unique identifier for this card instance.</summary>
    public string CardId { get; init; } = string.Empty;

    /// <summary>Type discriminator: "Connection", "Actions", "Latency", or "Sensor".</summary>
    public string CardType { get; init; } = string.Empty;

    /// <summary>HWiNFO sensor name this card is bound to (null for non-sensor cards).</summary>
    public string? SensorId { get; init; }

    /// <summary>Horizontal position within the saved layout.</summary>
    [JsonPropertyName("positionX")]
    public double Left { get; init; }

    /// <summary>Vertical position within the saved layout.</summary>
    [JsonPropertyName("positionY")]
    public double Top { get; init; }

    /// <summary>Current card width in pixels.</summary>
    public double Width { get; init; } = 220;

    /// <summary>Current card height in pixels.</summary>
    public double Height { get; init; } = 160;

    /// <summary>Relative render order within the layout.</summary>
    [JsonPropertyName("zIndex")]
    public int Layer { get; init; }

    /// <summary>Chosen telemetry view (Dashboard 2.0). <see cref="GraphType.Auto"/> = resolve from Kind/unit at render.</summary>
    public GraphType DisplayMode { get; init; } = GraphType.Auto;

    /// <summary>Second sensor bound for the <see cref="GraphType.DualMetric"/> overlay (null otherwise).</summary>
    public string? SecondarySensorId { get; init; }

    /// <summary>User-supplied display title overriding the raw sensor name (null = use the sensor name).</summary>
    public string? CustomTitle { get; init; }

    /// <summary>Whether the numeric value/unit overlay is shown on the card (false = ambient sparkline + title only).</summary>
    public bool ShowValueOverlay { get; init; } = true;

    /// <summary>Per-card color scheme override (null = the built-in "Default" preset).</summary>
    public SensorCardTheme? CardTheme { get; init; }

    /// <summary>Compatibility alias for older canvas-oriented callers.</summary>
    [JsonIgnore]
    public double PositionX
    {
        get => Left;
        init => Left = value;
    }

    /// <summary>Compatibility alias for older canvas-oriented callers.</summary>
    [JsonIgnore]
    public double PositionY
    {
        get => Top;
        init => Top = value;
    }

    /// <summary>Compatibility alias for older canvas-oriented callers.</summary>
    [JsonIgnore]
    public int ZIndex
    {
        get => Layer;
        init => Layer = value;
    }
}

/// <summary>
/// A complete dashboard layout profile, serialised to/from JSON.
/// </summary>
public record DashboardProfile
{
    /// <summary>Human-readable profile name (e.g. "Gaming Mode", "Idle Monitoring").</summary>
    public string ProfileName { get; init; } = "Default";

    /// <summary>Whether cards snap to the nearest grid line on drop.</summary>
    public bool IsSnapToGridEnabled { get; init; }

    /// <summary>Grid cell size in pixels (used when snapping is enabled).</summary>
    public int GridSize { get; init; } = 50;

    /// <summary>Persisted WebSocket host address for the remote connection.</summary>
    public string HostAddress { get; init; } = "wss://localhost:5005/ws";

    /// <summary>Persisted path to the Remex.Agent.exe binary or its containing directory.</summary>
    public string HostPath { get; init; } = string.Empty;

    /// <summary>Persisted application language (e.g. "en", "es", "hi").</summary>
    public string Language { get; init; } = "en";

    /// <summary>All card positions and sizes.</summary>
    public List<CardState> Cards { get; init; } = new();

    /// <summary>Sensor names pinned to the Home overview.</summary>
    public List<string> PinnedSensorIds { get; init; } = new();

    /// <summary>Screen identifiers whose contextual coach marks the user has already seen/dismissed (Dashboard 2.0).</summary>
    public List<string> SeenCoachMarks { get; init; } = new();

    /// <summary>Persisted Wake-on-LAN target MAC address (e.g. "AA:BB:CC:DD:EE:FF").</summary>
    public string WolMacAddress { get; init; } = string.Empty;

    /// <summary>Persisted Wake-on-LAN broadcast IP (default "255.255.255.255").</summary>
    public string WolBroadcastIp { get; init; } = "255.255.255.255";

    /// <summary>Persisted Wake-on-LAN UDP port (default 9).</summary>
    public int WolPort { get; init; } = 9;

    /// <summary>Whether the user has completed the first-run tutorial.</summary>
    public bool HasCompletedTutorial { get; init; }

    /// <summary>Whether infinite/decorative animations should be suppressed for accessibility.</summary>
    public bool IsReducedMotion { get; init; }

    /// <summary>
    /// When true, closing the main window (the X button) hides it to the system tray
    /// and keeps the app running. When false, the X button exits the app entirely.
    /// Defaults to true to preserve the classic tray-resident behavior.
    /// </summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>
    /// When true, the desktop app checks the public GitHub Releases API for a newer RemEx build
    /// on startup and shows a notice in About. PC-only; the Android client ignores it. Defaults to
    /// true — the check is a single anonymous request to api.github.com with no telemetry.
    /// </summary>
    public bool CheckForUpdatesAutomatically { get; init; } = true;

    /// <summary>Host screen capture JPEG quality (10–100). Applies to the stream sent to mobile clients.</summary>
    public int StreamQuality { get; init; } = 100;

    /// <summary>Host screen capture target frames per second (5–360; 360 is the "Unlimited" ceiling — see <see cref="DesktopConfig.MaxTargetFps"/>).</summary>
    public int StreamFps { get; init; } = 30;

    /// <summary>Host screen capture scale factor sent to the connected client (0.25–1.0).</summary>
    public double StreamScale { get; init; } = 1.0;

    /// <summary>Visual aesthetic and theme overrides.</summary>
    public CustomizationSettings Customization { get; init; } = new();

    /// <summary>Configured sensor threshold alerts.</summary>
    public List<SensorAlert> SensorAlerts { get; init; } = new();

    /// <summary>Recently used connection endpoints (most-recent first, capped at 10).</summary>
    public List<ConnectionProfile> ConnectionHistory { get; init; } = new();
}

/// <summary>
/// A single entry in the connection history list.
/// </summary>
public record ConnectionProfile
{
    /// <summary>Display name (defaults to the host address).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>WebSocket host address (e.g. "ws://192.168.1.10:5005/ws").</summary>
    public string HostAddress { get; init; } = string.Empty;

    /// <summary>When this host was last successfully connected to.</summary>
    public System.DateTime LastConnected { get; init; }
}

/// <summary>
/// The values <see cref="CustomizationSettings.ThemeMode"/> can carry (RemEx-zk5bc).
/// </summary>
/// <remarks>
/// String constants rather than an enum because the field rides source-generated JSON in a record
/// that tolerates unknown values by design — an enum would turn a profile written by a newer build
/// into a deserialization failure instead of a value the reader falls back from.
/// </remarks>
public static class ThemeModes
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string System = "System";
}

/// <summary>The values <see cref="CustomizationSettings.ColorSource"/> can carry.</summary>
/// <remarks>String constants for the same reason as <see cref="ThemeModes"/>: the record tolerates
/// unknown values by design, and the desktop resolves an unavailable source to Custom at load.</remarks>
public static class ColorSources
{
    public const string WindowsAccent = "WindowsAccent";
    public const string Wallpaper = "Wallpaper";
    public const string Custom = "Custom";
}

/// <summary>The values <see cref="CustomizationSettings.WallpaperSource"/> can carry.</summary>
public static class WallpaperSources
{
    public const string Desktop = "Desktop";
    public const string Image = "Image";
}

/// <summary>
/// A whole palette recipe the person chose to keep: where the seed came from, the seed itself, and
/// the three shaping inputs. Applying one reproduces the palette; it is not the palette.
/// </summary>
public record SavedPalette
{
    /// <summary>The English prefix the 2→3 migration names converted swatches with ("Palette 1",
    /// "Palette 2", …). Plain English on purpose: this assembly has no localisation, and the person
    /// renames the palette on the sheet.</summary>
    public const string DefaultNamePrefix = "Palette ";

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>A <see cref="ColorSources"/> value.</summary>
    [JsonPropertyName("colorSource")]
    public string ColorSource { get; init; } = ColorSources.Custom;

    /// <summary>The seed hex, e.g. "#6C4CFF".</summary>
    [JsonPropertyName("seed")]
    public string Seed { get; init; } = "#6C4CFF";

    /// <summary>The seed chroma (the Vibrancy slider).</summary>
    [JsonPropertyName("vibrancy")]
    public double Vibrancy { get; init; } = 48.0;

    /// <summary>The contrast target, -1.0 to 1.0.</summary>
    [JsonPropertyName("contrast")]
    public double Contrast { get; init; }

    /// <summary>One of the seven Android strategy names (the desktop normalises anything else).</summary>
    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = "TonalSpot";
}

/// <summary>
/// Persisted visual customization parameters.
/// </summary>
public record CustomizationSettings
{
    /// <summary>
    /// Which shape of this record was written. <c>0</c> — the value an absent key deserialises to —
    /// means "written before the seed engine existed".
    /// </summary>
    /// <remarks>
    /// THE ONLY HONEST WAY TO ASK "HAS THIS PROFILE BEEN MIGRATED YET". Every other signal is
    /// ambiguous: an absent <c>ThemeContrast</c> and a deliberate 0.0 deserialise to the same
    /// double, an absent <c>SchemeVariant</c> and a deliberate TonalSpot to the same string. A
    /// migration that guesses from those either re-runs on every launch — overwriting the user's
    /// choices with the preset's every time — or never runs at all. A version stamp is one integer
    /// and it makes the question exact.
    /// <para>
    /// The desktop owns the migration itself, because deciding what a legacy <c>ThemeId</c> means
    /// requires the preset catalogue and the palette generator, both of which live there. This
    /// record only carries the stamp. See <c>Remex.Desktop.Services.CustomizationMigration</c>.
    /// </para>
    /// </remarks>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    /// <summary>Identifier for the selected presentation style.</summary>
    [JsonPropertyName("baseTheme")]
    public string ThemeId { get; init; } = "BaseDarkGlass";

    /// <summary>Corner radius in pixels for cards and panels.</summary>
    public double CornerRadius { get; init; } = 16;

    /// <summary>Corner radius in pixels for remote control buttons.</summary>
    public double RemoteCardCornerRadius { get; init; } = 24;

    /// <summary>Opacity (0.0 to 1.0) of the canvas cards.</summary>
    public double GlassOpacity { get; init; } = 0.1;

    /// <summary>Opacity (0.1 to 1.0) of the entire app window when Glass mode is active.</summary>
    public double AppWindowOpacity { get; init; } = 0.92;

    /// <summary>Relative strength of the neon/glow effects.</summary>
    public double GlowStrength { get; init; } = 2;

    /// <summary>Vibrancy (Chroma) level for the seed color (Material 3).</summary>
    public double ThemeSeedChroma { get; init; } = 48.0;

    /// <summary>Contrast level for the dynamic color scheme (-1.0 to 1.0).</summary>
    public double ThemeContrast { get; init; } = 0.0;

    /// <summary>Primary brand/accent colour in Hex (e.g. "#6C4CFF").</summary>
    public string AccentColor { get; init; } = "#6C4CFF";

    /// <summary>Recently-used seeds (hex strings). Schema 3 converted the pre-existing entries into <see cref="SavedPalettes"/> and emptied this list once; the Custom source's recents row writes it again afterwards.</summary>
    public IReadOnlyList<string> CustomAccentColors { get; init; } = Array.Empty<string>();

    /// <summary>Material 3 scheme variant for the Dynamic theme.</summary>
    public string SchemeVariant { get; init; } = "TonalSpot";

    /// <summary>Who writes <see cref="AccentColor"/>: a <see cref="ColorSources"/> value. New
    /// profiles start on the Windows accent; the desktop resolves an unavailable source (Linux) to
    /// Custom without persisting it (RemEx-ddynd).</summary>
    [JsonPropertyName("colorSource")]
    public string ColorSource { get; init; } = ColorSources.WindowsAccent;

    /// <summary>Which extracted wallpaper candidate was chosen. Out of range resets to 0 at load.</summary>
    [JsonPropertyName("wallpaperSeedIndex")]
    public int WallpaperSeedIndex { get; init; }

    /// <summary>A <see cref="WallpaperSources"/> value: the desktop's own wallpaper, or a picked image.</summary>
    [JsonPropertyName("wallpaperSource")]
    public string WallpaperSource { get; init; } = WallpaperSources.Desktop;

    /// <summary>Path of the APP-OWNED copy of a picked image, never the original file.</summary>
    [JsonPropertyName("wallpaperImagePath")]
    public string? WallpaperImagePath { get; init; }

    /// <summary>Wallpaper blur, 0 to 1, mapped to a blur radius by the desktop.</summary>
    [JsonPropertyName("wallpaperBlur")]
    public double WallpaperBlur { get; init; } = 0.6;

    /// <summary>The person's saved palettes, in the order they were saved.</summary>
    [JsonPropertyName("savedPalettes")]
    public IReadOnlyList<SavedPalette> SavedPalettes { get; init; } = Array.Empty<SavedPalette>();

    /// <summary>
    /// Whether the generated palette is a light one. <c>null</c> means "decide from
    /// <see cref="ThemeId"/>", which is how every profile written before this key existed behaves.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY NULLABLE, AND THAT IS THE WHOLE MIGRATION. Light/dark used to be a property of
    /// the preset name — the code asked <c>ThemeId == "SolarFlare"</c> — so a user who picked a
    /// light preset and then changed the seed silently got a dark palette. Making it a real setting
    /// has to not repaint every existing install on upgrade: an absent key deserialises to null,
    /// null reproduces the old name-based answer exactly, and the first time the user touches the
    /// switch it becomes explicit and stays that way.
    /// </remarks>
    [JsonPropertyName("useLightPalette")]
    public bool? UseLightPalette { get; init; }

    /// <summary>
    /// Base palette mode: <c>"Light"</c>, <c>"Dark"</c>, or <c>"System"</c> (follow the OS
    /// setting, live). <c>null</c> means the profile predates the mode (RemEx-zk5bc).
    /// </summary>
    /// <remarks>
    /// SUPERSEDES <see cref="UseLightPalette"/>, which stays as a migration input only and is
    /// never written with a new value again — RemEx-dbkzy stamped it explicit on every migrated
    /// profile, so its null channel was consumed and a third state ("follow the OS") could not
    /// ride on it. Migration arm 2 stamps this field from it; when this is <c>null</c> the reader
    /// falls back to the <see cref="UseLightPalette"/>-then-preset chain exactly as before, so an
    /// unmigrated profile paints what it always painted.
    /// </remarks>
    [JsonPropertyName("themeMode")]
    public string? ThemeMode { get; init; }

    /// <summary>Requested background treatment: Aurora (default), Wallpaper, Acrylic, Glass, Gradient or Solid. The desktop resolves anything else at load.</summary>
    [JsonPropertyName("canvasBackgroundType")]
    public string BackgroundMaterial { get; init; } = "Aurora";

    /// <summary>When true, the UI accent color attempts to sync with physical hardware (OpenRGB/FanControl).</summary>
    public bool SyncWithHardware { get; init; } = false;

    /// <summary>Selected splash screen animation sequence style.</summary>
    [JsonPropertyName("splashStyle")]
    public string SplashStyle { get; init; } = "CosmicZoom";

    /// <summary>Font family for page-title headers (an avares URI for a bundled font, or a system font name).</summary>
    [JsonPropertyName("pageTitleFont")]
    public string PageTitleFontFamily { get; init; } = "avares://Remex.Desktop/Assets/Fonts#Orbitron";

    /// <summary>Font family for card / section headers. Empty = inherit the app default. (Reserved for the card-header tier.)</summary>
    [JsonPropertyName("cardHeaderFont")]
    public string CardHeaderFontFamily { get; init; } = "";

    /// <summary>Font family for body / content text (an avares URI for a bundled font, or a system font name). Default = Inter (the app default).</summary>
    [JsonPropertyName("bodyFont")]
    public string BodyFontFamily { get; init; } = "avares://Avalonia.Fonts.Inter/Assets#Inter";

    /// <summary>Overall UI scale (text and everything else), applied as a layout transform on the shell. 1.0 = 100%.</summary>
    [JsonPropertyName("uiScale")]
    public double UiScale { get; init; } = 1.0;

    /// <summary>Compatibility alias for older UI clients.</summary>
    [JsonIgnore]
    public string BaseTheme
    {
        get => ThemeId;
        init => ThemeId = value;
    }

    /// <summary>Compatibility alias for older UI clients.</summary>
    [JsonIgnore]
    public string CanvasBackgroundType
    {
        get => BackgroundMaterial;
        init => BackgroundMaterial = value;
    }
}
