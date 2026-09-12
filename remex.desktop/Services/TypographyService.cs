using Avalonia;
using Avalonia.Controls;
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
/// ONE KEY IS NOT IN THE DICTIONARY. Untagged text's size is the OWN key
/// <c>MaterialDesignFontSize</c> (App.axaml:47), written in place like ThemeService writes
/// <c>UiScale</c> (ThemeService.cs:576-603) — an own key shadows every merged dictionary. Untagged
/// text's bold USED to be a runtime <see cref="Avalonia.Styling.Style"/> attached/detached on
/// <c>Application.Styles</c> only while Body bold was on; that mutation of the live Styles
/// collection broke UI Automation's <c>FindAll</c> on the shell root until restart
/// (RemEx-jt6w5.11). It is now <see cref="TypographyResolver.UntaggedBoldFontWeightKey"/>, a
/// resource key like every other <c>Typo.*</c> value: present (Bold) only while Body bold is on,
/// absent (never "set to Regular") when off, so the default <c>{x:Type TextBlock}</c> ControlTheme's
/// <c>DynamicResource</c> setter (<c>Styles/Typography.axaml</c>) resolves to Unset and "off" means
/// "inherit" — Material buttons keep the Medium their content TextBlock inherits.
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

        // ALWAYS present (never absent) — see TypographyResolver.UntaggedBoldFontWeightKey's remarks
        // for why: an already-materialized TextBlock's DynamicResource binding does not re-evaluate
        // the first time an absent key starts existing, only when a present key's value changes.
        // AvaloniaProperty.UnsetValue is the sentinel that makes the ControlTheme's FontWeight setter
        // contribute nothing, same effect as "no key" without ever removing the key.
        _overrideResources[TypographyResolver.UntaggedBoldFontWeightKey] =
            resolved.UntaggedBold ? FontWeight.Bold : AvaloniaProperty.UnsetValue;

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
    }
}
