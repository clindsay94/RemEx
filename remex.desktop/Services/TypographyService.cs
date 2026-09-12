using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>
/// The Avalonia half of Personalize → Text (RemEx-jt6w5): writes what <see cref="TypographyResolver"/>
/// computes into an override <see cref="ResourceDictionary"/> merged into
/// <c>Application.Current.Resources</c> — the same mechanism <see cref="ThemeService"/> uses for the
/// palette — so every <c>{DynamicResource Typo.*}</c> setter in <c>Styles/Typography.axaml</c>
/// re-resolves live.
/// </summary>
/// <remarks>
/// <para>
/// OWNED BY <see cref="ThemeService"/>, APPLIED FROM <c>ApplyCustomizationCore</c>. That is the one
/// moment the palette is regenerated — on profile load and on every settings change — and it is
/// where the palette's surface colour is in hand, which the halo derives its colour from.
/// </para>
/// <para>
/// UI thread only in production (reached through ThemeService's posted apply); null-tolerant on
/// <c>Application.Current</c> so a unit test can call <see cref="Apply"/> and read
/// <see cref="Overrides"/> directly — this assembly's tests have no Avalonia.Headless.
/// </para>
/// <para>
/// THE CONSTRUCTOR APPLIES THE DEFAULTS AND MERGES THEM. In production
/// (<c>App.axaml.cs</c>'s <c>ApplyThemeBeforeWindowShown</c>, called from
/// <c>OnFrameworkInitializationCompleted</c>) <c>Application.Current</c> is already live when
/// <see cref="ThemeService"/> is constructed, so this constructor's own <see cref="Apply"/> call
/// merges <see cref="Overrides"/> immediately — before any window exists, so every <c>Typo.*</c>
/// key resolves from the first paint. There is no XAML copy of the defaults to drift (one table:
/// <see cref="TypographyResolver.Members"/>). See the <c>Contains</c> guard inside <see cref="Apply"/>
/// for why that merge and <see cref="ThemeService"/>'s own constructor-post merge don't collide
/// regardless of which one happens to run first.
/// </para>
/// <para>
/// UNTAGGED BOLD IS NOT IN THE DICTIONARY, AND NOT A RESOURCE AT ALL — <see cref="ApplyUntaggedBold"/>
/// sets or clears a plain INHERITED <c>TextBlock.FontWeightProperty</c> local value directly on
/// every open top-level window's root. Two other mechanisms were tried and MEASURED broken before
/// this one (RemEx-jt6w5.11, two review rounds):
/// </para>
/// <para>
/// ROUND 0 (shipped, then reverted): a runtime <see cref="Avalonia.Styling.Style"/>
/// attached/detached on <c>Application.Styles</c> only while Body bold was on. That mutation of the
/// live Styles collection broke UI Automation's <c>FindAll</c> on the shell root until restart —
/// confirmed against the real Windows UIA stack.
/// </para>
/// <para>
/// ROUND 1 (never shipped, caught by a headless test before it could): a resource key
/// (<c>Typo.UntaggedBold.FontWeight</c>) the default <c>{x:Type TextBlock}</c> ControlTheme read via
/// <c>DynamicResource</c>, toggling between <c>FontWeight.Bold</c> and
/// <c>AvaloniaProperty.UnsetValue</c>. Measured (<c>ShellTypographyBoldAutomationTests</c>,
/// <c>GetDiagnostic</c>) that the Unset value does NOT make the ControlTheme setter a no-op the way
/// an absent <c>DynamicResource</c> key does for every other <c>Typo.*</c> setter in this file — it
/// registers a real Style-priority value frame that resolves to the property's own default
/// (Regular), and that frame outranks a Button's OWN Style-priority FontWeight setter for its
/// content TextBlock. Baseline reading on <c>ConnectionStatusButton</c>'s StatusText (declared
/// SemiBold via its own <c>.secondary</c> class): <c>Value=Normal, Priority=Style</c> — Body bold OFF
/// was silently forcing Regular onto text that should have kept its own weight, the exact regression
/// this bead exists to forbid.
/// </para>
/// <para>
/// ROUND 2 (current): plain property inheritance, no resource, no Style mutation. "Off" clears the
/// value (nothing to inherit; a real Button's own nearer Style-priority setter is unaffected since
/// inheritance only ever supplies a value where none exists closer to the element). "On" sets Bold
/// on the window root; only text with no nearer ancestor supplying <c>FontWeightProperty</c> —
/// genuinely untagged text — inherits it. Untagged text's size is still the OWN key
/// <c>MaterialDesignFontSize</c> (App.axaml:47), written in place like ThemeService writes
/// <c>UiScale</c> (ThemeService.cs:576-603) — an own key shadows every merged dictionary; that part
/// was never broken and is unchanged.
/// </para>
/// </remarks>
public sealed class TypographyService
{
    /// <summary>App.axaml:47 — the x:Double the Window and popup roots read their FontSize from.</summary>
    public const string DefaultFontSizeKey = "MaterialDesignFontSize";

    /// <summary>The surface used before the first palette lands (a Dark-mode M3 surface tone).</summary>
    public static readonly Color DefaultSurface = Color.FromRgb(0x12, 0x12, 0x12);

    private readonly ResourceDictionary _overrideResources = new();

    public TypographyService()
    {
        Apply(TypographySettings.Default, DefaultSurface);
    }

    /// <summary>The merged override dictionary; ThemeService merges it, tests read it.</summary>
    internal ResourceDictionary Overrides => _overrideResources;

    /// <summary>What the last <see cref="Apply"/> computed.</summary>
    public TypographyResolution? LastApplied { get; private set; }

    /// <summary>
    /// Resolves <paramref name="settings"/> against <paramref name="surface"/> (the palette's
    /// surface colour, <c>palette.Surface</c> in ThemeService) and publishes every resource.
    /// Batched like ThemeService: detach, repopulate, reattach — one ResourcesChanged.
    /// </summary>
    public void Apply(TypographySettings settings, Color surface)
    {
        var resolved = TypographyResolver.Resolve(settings, surface);
        LastApplied = resolved;

        var app = Application.Current;
        var merged = app?.Resources.MergedDictionaries;
        // Contains-guarded because either this constructor's own Apply(...) or ThemeService's
        // constructor-post Add can be the FIRST merge, depending on construction order —
        // production (App.axaml.cs's ApplyThemeBeforeWindowShown, called from
        // OnFrameworkInitializationCompleted) constructs ThemeService with Application.Current
        // already live, so THIS Apply call (run from the TypographyService field initializer,
        // before ThemeService's own constructor body executes) merges the dictionary first; a
        // unit test with no Application.Current merges neither; only in an ordering where
        // ThemeService's deferred post runs before this method ever executes would that post go
        // first. Whichever runs first, an unconditional Add on the second would find the
        // dictionary already parented and throw "The ResourceDictionary already has a parent"
        // (measured via remex.desktop.render.tests) — so both sides guard with Contains
        // (ThemeService.cs's own merge, next to `Typography.Overrides`, does the same).
        if (merged is { } m1 && m1.Contains(_overrideResources)) m1.Remove(_overrideResources);
        _overrideResources.Clear();

        foreach (var (key, size) in resolved.FontSizes) _overrideResources[key] = size;
        foreach (var (key, weight) in resolved.FontWeights) _overrideResources[key] = weight;

        // One DropShadowEffect per apply, shared by every shadowed section (an effect can be
        // referenced by any number of visuals). A section with no halo gets NO key: the
        // DynamicResource setter then resolves to Unset and the TextBlock keeps Effect = null,
        // which is what makes "off" free — no offscreen pass, not a transparent shadow.
        DropShadowEffect? effect = null;
        foreach (var (section, shadow) in resolved.SectionShadows)
        {
            if (shadow is null) continue;
            effect ??= new DropShadowEffect
            {
                OffsetX = 0,
                OffsetY = 1,
                BlurRadius = shadow.BlurRadius,
                Color = shadow.Color,
                Opacity = shadow.Opacity,
            };
            _overrideResources[TypographyResolver.ShadowKey(section)] = effect;
        }

        _overrideResources[TypographyResolver.SensorTitleBackdropKey] = resolved.SensorTitleBackdrop;

        if (merged is { } m2 && !m2.Contains(_overrideResources)) m2.Add(_overrideResources);

        if (app is null) return;

        app.Resources[DefaultFontSizeKey] = resolved.DefaultFontSize;

        ApplyUntaggedBold(app, resolved.UntaggedBold);
    }

    /// <summary>
    /// See the class remarks (ROUND 2) for why this is a plain inherited local value on each open
    /// window's root rather than a resource or a Styles-collection mutation. Windows opened AFTER
    /// this call pick up the current state the next time <see cref="Apply"/> runs (the same settings
    /// change that would show a newly-opened window a stale Body-bold state today, before this
    /// method existed, for every other Typo.* value too) — there is no window-opened hook in this
    /// codebase to extend (checked: <c>ThemeService</c> does not maintain one either; it relies
    /// entirely on Application-wide resources/styles, which is exactly the mechanism ROUND 2 could
    /// not use here).
    /// </summary>
    private static void ApplyUntaggedBold(Application app, bool untaggedBold)
    {
        if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        foreach (var window in desktop.Windows)
        {
            if (untaggedBold) window.SetValue(TextBlock.FontWeightProperty, FontWeight.Bold);
            else window.ClearValue(TextBlock.FontWeightProperty);
        }
    }
}
