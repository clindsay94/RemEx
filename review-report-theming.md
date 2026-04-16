## Code-Review-Report.md — Avalonia Desktop Theming Fixes
---
type: Bugfix
severity: High
breaking_changes: False
target_files:
  - Remex.Core/Models/DashboardProfile.cs
  - Remex.Client/Services/ThemeService.cs
  - Remex.Client/MainWindow.axaml.cs
  - Remex.Client/ViewModels/CustomizationViewModel.cs
  - Remex.Client/Views/CustomizationView.axaml
  - Remex.Client/Views/CanvasView.axaml
  - Remex.Client/Views/SettingsView.axaml
  - Remex.Client/Views/ShellView.axaml
  - Remex.Client/Views/TrayFlyoutWindow.axaml
  - Remex.Client/Controls/DashboardBackgroundControl.axaml
  - Remex.Client/Localization/Strings.resx
---

## Issue Summary

Six interconnected theming bugs were identified and fixed: (1) the Glass Opacity slider controlled a background tint rectangle instead of card transparency; (2) the Glass atmosphere mode never enabled real window-level see-through transparency on any platform; (3) the Wallpaper aurora effect was nearly invisible due to very low hardcoded opacity values; (4) telemetry cards, toolbar, and text elements referenced Color resources where Brush resources were needed, silently failing to receive M3 theme updates; (5) the connection banner had hardcoded dark colors; and (6) TrayFlyout (Live Glance) widget cards used hardcoded #1AFFFFFF backgrounds.

---

## Root Cause Analysis

### Bug 1 — Card opacity slider did nothing to cards
DynamicResource CardBackground is a Color resource. DynamicResource CardBackgroundBrush is the corresponding SolidColorBrush. The DraggableCard template style used Background="{DynamicResource CardBackground}" — in Avalonia, Border.Background is IBrush?, not Color. Avalonia does not automatically coerce a Color DynamicResource to a SolidColorBrush at runtime. The property silently received no value, leaving cards without background. Additionally the "Glass Opacity" slider was wired to the dashboard background tint rectangle, not to card surfaces at all.

### Bug 2 — Glass mode was not transparent
MainWindow.axaml.cs only requested WindowTransparencyLevel.Mica/AcrylicBlur for Windows modes. The "Glass" branch fell through to the else clause: TransparencyLevelHint=[None], Background=SolidColorBrush(#0A0A10). The window was always fully opaque.

### Bug 3 — Wallpaper aurora was invisible
Three animated RadialGradientBrush layers had max opacities of 0.45/0.35/0.25 over a dark BackgroundGradientBrush base. Colors were hardcoded (#406C4CFF etc.) and did not use the current theme accent colors, making the effect both invisible and theme-blind.

### Bug 4 — Color-vs-Brush mismatches
CanvasView, SettingsView, and ShellView used {DynamicResource TextMuted}, TextPrimary, TextSecondary, GlassBaseDark, and CardBorder on Foreground/Background/BorderBrush properties — all typed as IBrush in Avalonia. The bare Color keys cannot be used as Brushes and silently produce no output.

### Bug 5 — Connection banner hardcoded colors
ShellView.axaml Background="#CC1A1A0A" and BorderBrush="#806B4CFF" were constant hex values. ThemeService updating GlassBaseDarkBrush and AccentPrimaryBrush had no effect on the banner.

### Bug 6 — TrayFlyout widget cards hardcoded
Window.Styles had Background="#1AFFFFFF" for widget-card. No connection to CardBackgroundBrush. Theme changes had no effect on the tray popup.

---

## Changes Made

### Remex.Core/Models/DashboardProfile.cs
Added AppWindowOpacity property (double, default 0.92) to CustomizationSettings.

### Remex.Client/Services/ThemeService.cs
Exposes AppWindowOpacity as a resource override.
CardBackgroundBrush and CardBackgroundHoverBrush now incorporate GlassOpacity as the alpha channel of the M3 SurfaceContainer color so the renamed Card Opacity slider affects card transparency directly.

### Remex.Client/MainWindow.axaml.cs
New Glass branch: TransparencyLevelHint=[Transparent,Blur], Background=Transparent, Opacity=AppWindowOpacity.
Other modes explicitly reset Opacity=1.0.

### Remex.Client/ViewModels/CustomizationViewModel.cs
Added AppWindowOpacity observable property + change handler.
Added IsGlassModeSelected computed property to show/hide the Window Opacity slider.
CanvasBackgroundType change handler notifies IsGlassModeSelected.
ApplyAndSave includes AppWindowOpacity.

### Remex.Client/Views/CustomizationView.axaml
Added Window Opacity slider row in the Atmosphere section, bound to AppWindowOpacity, visible only when IsGlassModeSelected is true.

### Remex.Client/Localization/Strings.resx
Custom_GlassOpacity value changed from "Glass Opacity" to "Card Opacity".
Added Custom_AppWindowOpacity = "Window Opacity".

### Remex.Client/Views/CanvasView.axaml
DraggableCard style: CardBackground (Color) changed to CardBackgroundBrush; CardBorder changed to CardBorderBrush.
Toolbar Border.Background: GlassBaseDark changed to GlassBaseDarkBrush.
All bare TextMuted/TextPrimary/TextSecondary Foreground references corrected to *Brush variants.

### Remex.Client/Views/SettingsView.axaml
All GlassBaseDark backgrounds, CardBorder border brushes, and TextMuted/TextPrimary/TextSecondary foregrounds corrected to *Brush equivalents. TextBox controls now show text in themed M3 colors.

### Remex.Client/Views/ShellView.axaml
Connection banner Background="#CC1A1A0A" replaced with {DynamicResource GlassBaseDarkBrush}.
Connection banner BorderBrush="#806B4CFF" replaced with {DynamicResource AccentPrimaryBrush}.

### Remex.Client/Views/TrayFlyoutWindow.axaml
widget-card style: #1AFFFFFF replaced with {DynamicResource CardBackgroundBrush}, #20FFFFFF replaced with {DynamicResource CardBorderBrush}.
Main border: #30FFFFFF replaced with {DynamicResource CardBorderBrush}.

### Remex.Client/Controls/DashboardBackgroundControl.axaml
Wallpaper/Aurora: All hardcoded gradient colors replaced with {DynamicResource AccentPrimary/AccentHover/AccentPressed}. Max layer opacities raised to 0.70/0.55/0.42. Aurora is now clearly visible and changes with the theme accent color.
Glass mode: Replaced opaque dark gradient + accent tint with 25%-opacity GlassBaseDarkBrush + 8%-opacity AccentPrimaryBrush frost hint since the window is now transparent when Glass is active.

---

## Validation

dotnet build Remex.Client.Desktop/Remex.Client.Desktop.csproj: Build succeeded. 0 Warning(s). 0 Error(s).
dotnet test Remex.Client.Tests/: Passed. Failed: 0, Passed: 25, Skipped: 0.

---

## Remaining Manual Steps

1. Locale files — Custom_AppWindowOpacity is missing from all non-English .resx files. Falls back to English at runtime. Translators should add entries.
2. Compositor support for Glass mode — WindowTransparencyLevel.Transparent on Linux requires KWin or another compositor with alpha support. The frost overlay keeps the UI readable in environments without compositing.
3. Preset defaults for AppWindowOpacity — Preset cases in CustomizationViewModel.SelectTheme() do not set AppWindowOpacity. Glass and CyberNOC presets may benefit from a default lower value (e.g. 0.85).
