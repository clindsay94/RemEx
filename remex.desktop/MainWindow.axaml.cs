using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Remex.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using Remex.Core.Models;

namespace Remex.Desktop;

public partial class MainWindow : Window
{
    private ThemeService? _themeService;
    private ColorSourceCoordinator? _colorSources;

    public MainWindow()
    {
        InitializeComponent();

        // THE ACRYLIC BACKDROP DEPENDS ON THIS LINE (RemEx-c437b). Without it the window
        // keeps Material.Avalonia's decorations theme, whose underlay is an opaque sheet over the
        // OS backdrop — the app then renders a flat surface while reporting that Acrylic is active.
        // Themes/Chrome/WindowChrome.axaml carries the diagnosis and why nothing lighter reaches it.
        // Assigned here rather than as a Window attribute because the theme arrives in a merged
        // resource dictionary, which only exists once InitializeComponent has run.
        // TryGetResource, NOT the Resources indexer. The indexer does not search
        // MergedDictionaries, so it returns null for a key that is plainly there — and because
        // WindowDecorationsTheme is a nullable ControlTheme, assigning that null is not an error.
        // The window then quietly keeps Material's decorations and the backdrop stays dead, which
        // is the same shape of silent failure this whole bug was. Missing means broken, so throw.
        if (!Resources.TryGetResource("BackdropSafeWindowDecorations", ActualThemeVariant, out var decorations)
            || decorations is not ControlTheme decorationsTheme)
        {
            throw new InvalidOperationException(
                "Themes/Chrome/WindowChrome.axaml did not supply BackdropSafeWindowDecorations. " +
                "Without it the OS backdrop is covered by Material's opaque decorations underlay.");
        }

        WindowDecorationsTheme = decorationsTheme;

        _themeService = App.Services.GetService<ThemeService>(); // optional service
        if (_themeService is not null)
        {
            _themeService.CustomizationApplied += OnCustomizationApplied;

            if (App.Services.GetService<DashboardLayoutService>() is { CurrentProfile.Customization: { } settings }) // optional service
            {
                OnCustomizationApplied(settings);
            }
        }

        _colorSources = App.Services.GetService<ColorSourceCoordinator>(); // optional, like the theme service
        if (_colorSources is not null)
        {
            Opened += (_, _) =>
            {
                _colorSources.Start();
                _colorSources.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
            };
            Activated += (_, _) => _colorSources.PollNow();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty)
            _colorSources?.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (App.IsShuttingDown)
        {
            base.OnClosing(e);
            return;
        }

        // Honor the user's chosen close behavior (Settings → General).
        var closeToTray = true;
        if (App.Services?.GetService<DashboardLayoutService>() is { CurrentProfile: { } profile }) // optional service
            closeToTray = profile.CloseToTray;

        if (closeToTray)
        {
            // Keep the app alive in the tray; the window just hides.
            // The user can exit via the tray menu or the in-app Exit button.
            e.Cancel = true;
            Hide();
            base.OnClosing(e);
            return;
        }

        // User wants the X button to fully exit: end the process.
        e.Cancel = true;
        base.OnClosing(e);
        App.RequestApplicationShutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_themeService is not null)
            _themeService.CustomizationApplied -= OnCustomizationApplied;
        base.OnClosed(e);
    }

    private void OnCustomizationApplied(CustomizationSettings settings)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Every branch also sets TransparencyBackgroundFallback to the opaque palette surface
            // (ThemeService overrides GlassBaseDark with palette.Surface, following the active
            // seed on both a light and a dark palette). That fallback is what actually paints
            // when the requested backdrop is unavailable (Win10, X11 without a compositor) - so
            // it must never be left at whatever translucent value preceded this customization.
            TransparencyBackgroundFallback = OpaqueSurfaceFallbackBrush();

            if (OperatingSystem.IsWindows() && settings.BackgroundMaterial == "Acrylic")
            {
                TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur };
                Background = Brushes.Transparent;
                Opacity = 1.0;
            }
            else if (settings.BackgroundMaterial == "Glass")
            {
                // Glass mode: request compositor-level transparency so the desktop is visible
                // behind the window. AppWindowOpacity further controls how much shows through.
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.Blur };
                Background = Brushes.Transparent;
                Opacity = Math.Clamp(settings.AppWindowOpacity, 0.1, 1.0);
            }
            else
            {
                // Gradient, Wallpaper, Solid, and all non-transparent modes.
                TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
                // Follows the theme's own base (ThemeService overrides GlassBaseDark with
                // palette.Surface) rather than one hardcoded near-black, which was the same
                // window colour on every seed (RemEx-fy0a).
                // OpaqueColor: this branch has just disabled transparency, so the background must
                // actually be opaque. GlassBaseDark carries alpha on some palettes, and the brush
                // form would have made a "non-transparent" window partially transparent.
                Background = OpaqueSurfaceFallbackBrush();
                Opacity = 1.0;
            }
        });
    }

    // Shared by every OnCustomizationApplied branch: an unavailable backdrop must fall back to
    // the opaque palette surface, never a translucent pre-customization value (RemEx-l2yqy).
    // Assigning the fallback in code makes it a LocalValue that outranks the XAML DynamicResource,
    // so it only stays current because ThemeService.ApplyCustomizationCore both writes the
    // GlassBaseDark overrides and raises CustomizationApplied. Move either one and a Win10 / X11
    // window without a compositor keeps painting the previous seed's Surface after a theme switch.
    private static SolidColorBrush OpaqueSurfaceFallbackBrush() =>
        new(ThemeResources.OpaqueColor("GlassBaseDark", Color.FromRgb(0x0A, 0x0A, 0x10)));
}
