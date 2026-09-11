using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
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
/// THE CONSTRUCTOR APPLIES THE DEFAULTS. The dictionary is merged by ThemeService's constructor post
/// before any window exists, so every <c>Typo.*</c> key resolves from the first paint; there is no
/// XAML copy of the defaults to drift (one table: <see cref="TypographyResolver.Members"/>).
/// </para>
/// <para>
/// TWO KEYS ARE NOT IN THE DICTIONARY. Untagged text's size is the OWN key
/// <c>MaterialDesignFontSize</c> (App.axaml:47), written in place like ThemeService writes
/// <c>UiScale</c> (ThemeService.cs:576-603) — an own key shadows every merged dictionary. Untagged
/// text's bold is a runtime <see cref="Style"/> attached only while Body bold is on: a
/// <c>{x:Type TextBlock}</c> ControlTheme setter would also fire when OFF, forcing Regular onto
/// text that inherits Medium from a Material button — "off" must mean "inherit", which no resource
/// value can express.
/// </para>
/// </remarks>
public sealed class TypographyService
{
    /// <summary>App.axaml:47 — the x:Double the Window and popup roots read their FontSize from.</summary>
    public const string DefaultFontSizeKey = "MaterialDesignFontSize";

    /// <summary>The surface used before the first palette lands (a Dark-mode M3 surface tone).</summary>
    public static readonly Color DefaultSurface = Color.FromRgb(0x12, 0x12, 0x12);

    private readonly ResourceDictionary _overrideResources = new();

    // Untagged TextBlocks only: Theme == null excludes every Material type-scale member (they carry
    // their own Typo.<Member>.FontWeight); the two class exclusions are Headers members that live
    // as App.axaml Styles rather than ControlThemes, so a later Style would otherwise outrank them.
    private readonly Style _untaggedBold = new(x => x.OfType<TextBlock>()
            .PropertyEquals(StyledElement.ThemeProperty, null)
            .Not(y => y.Class("page-title"))
            .Not(y => y.Class("card-title")))
    {
        Setters = { new Setter(TextBlock.FontWeightProperty, FontWeight.Bold) },
    };

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
        // Contains-guarded: ThemeService owns the FIRST merge (deferred onto Dispatcher.UIThread
        // from its own constructor, so it lands after every constructor on the stack has returned).
        // This constructor's own Apply(...) call can run with Application.Current already live (a
        // render test's headless Application starts before `new ThemeService()` executes), so an
        // unconditional Add here would race ThemeService's deferred one and throw "The
        // ResourceDictionary already has a parent" the same way _overrideResources's own history
        // shows (ShellRenderFixture's remarks). Idempotent either way: unit tests never merge at
        // all (no Application.Current), and every later re-apply detaches its own prior attach.
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

        var attached = app.Styles.Contains(_untaggedBold);
        if (resolved.UntaggedBold && !attached) app.Styles.Add(_untaggedBold);
        else if (!resolved.UntaggedBold && attached) app.Styles.Remove(_untaggedBold);
    }
}
