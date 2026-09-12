using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Remex.Core.Models;

namespace Remex.Desktop.Services;

/// <summary>The four rows of Personalize → Text (spec § Sections and what they cover).</summary>
public enum TypographySection
{
    Headers,
    Body,
    Small,
    Sensor,
}

/// <summary>
/// One row of the type table: a type-scale member, the section that owns it, and its OWN base size
/// and weight — the values Material.Avalonia 3.19.0 ships for the six Material members
/// (Material.Styles/Resources/Themes/TextBlock.axaml) and App.axaml declares for the three kept
/// classes. "Bold off" returns to <see cref="BaseWeight"/>, never to Regular.
/// </summary>
public sealed record TypographyMember(string Key, TypographySection Section, double BaseSize, FontWeight BaseWeight)
{
    public string FontSizeKey => $"Typo.{Key}.FontSize";
    public string FontWeightKey => $"Typo.{Key}.FontWeight";
}

/// <summary>The legibility halo for one section: opaque surface colour, blur in px, effect opacity.</summary>
public sealed record TextShadow(Color Color, double BlurRadius, double Opacity);

/// <summary>Everything <see cref="TypographyService"/> writes, computed without Avalonia.</summary>
/// <param name="FontSizes"><c>Typo.&lt;Member&gt;.FontSize</c> → points.</param>
/// <param name="FontWeights"><c>Typo.&lt;Member&gt;.FontWeight</c> → weight.</param>
/// <param name="SectionShadows">Per section; <c>null</c> means "no Effect resource" for that section.</param>
/// <param name="DefaultFontSize">What untagged text inherits: the <c>MaterialDesignFontSize</c> own key.</param>
/// <param name="UntaggedBold">
/// Whether untagged text should be Bold (Body bold on) — the same source
/// <see cref="TypographyResolver.UntaggedBoldFontWeightKey"/>'s entry in <see cref="FontWeights"/>
/// is computed from in the same <see cref="TypographyResolver.Resolve"/> call; kept here too as the
/// readable flag tests and callers reach for. See <see cref="TypographyResolver.UntaggedBoldFontWeightKey"/>'s
/// remarks for the mechanism (RemEx-jt6w5.11) this key is part of.
/// </param>
/// <param name="SensorTitleBackdrop">The <c>Typo.Sensor.TitleBackdrop</c> flag.</param>
public sealed record TypographyResolution(
    IReadOnlyDictionary<string, double> FontSizes,
    IReadOnlyDictionary<string, FontWeight> FontWeights,
    IReadOnlyDictionary<TypographySection, TextShadow?> SectionShadows,
    double DefaultFontSize,
    bool UntaggedBold,
    bool SensorTitleBackdrop);

/// <summary>
/// Pure computation for Personalize → Text (RemEx-jt6w5): the ONE defaults table and the mapping
/// from <see cref="TypographySettings"/> to resource values. No Avalonia application, so every
/// rule here is unit-tested; <see cref="TypographyService"/> only writes what this returns.
/// </summary>
public static class TypographyResolver
{
    /// <summary>The app-wide default (App.axaml:47, <c>MaterialDesignFontSize</c>) that untagged text inherits.</summary>
    public const double DefaultFontSizeBase = 14;

    /// <summary>Bound by the sensor-card templates' backdrop Border (<c>IsVisible</c>).</summary>
    public const string SensorTitleBackdropKey = "Typo.Sensor.TitleBackdrop";

    /// <summary>
    /// Untagged text's Bold (RemEx-jt6w5.11). THE MECHANISM, after two abandoned attempts:
    /// <para>
    /// THE ORIGINAL MECHANISM (shipped, then reverted): a runtime <see cref="Avalonia.Styling.Style"/>
    /// attached/detached on <c>Application.Styles</c> only while Body bold was on. That mutation of
    /// the live Styles collection broke UI Automation's <c>FindAll</c> on the shell root until
    /// restart — confirmed against the real Windows UIA stack.
    /// </para>
    /// <para>
    /// ATTEMPT 1 (never shipped): a <c>FontWeight</c> Setter on the default
    /// <c>{x:Type TextBlock}</c> ControlTheme, bound to this key, toggling its value between
    /// <c>FontWeight.Bold</c> and <c>AvaloniaProperty.UnsetValue</c>. Measured
    /// (<c>ShellTypographyBoldAutomationTests</c>, <c>GetDiagnostic</c>) that the Unset value does
    /// NOT make a ControlTheme setter a no-op: it registers a real Style-priority value frame that
    /// resolves to the property's own default (Regular), outranking a Button's own Style-priority
    /// FontWeight setter for its content TextBlock — a forced-Regular regression.
    /// </para>
    /// <para>
    /// ATTEMPT 2 (never shipped): <see cref="TypographyService"/> pushed the inherited weight
    /// directly onto each already-open top-level window's root at Apply time. Measured broken for a
    /// different reason: both startup Apply calls run before <c>MainWindow</c> exists, so a
    /// persisted Body-bold-on opened the shell Regular at cold start until the next settings change
    /// — and the same gap applied to every on-demand window (<c>TrayFlyoutWindow</c>,
    /// <c>PairingDialog</c>, <c>CommandPaletteWindow</c>, etc.) created after the last Apply.
    /// </para>
    /// <para>
    /// THE MECHANISM: this key is ALWAYS PRESENT — <c>FontWeight.Bold</c> when Body bold is on,
    /// <c>FontWeight.Normal</c> (an explicit, ordinary value, never <c>AvaloniaProperty.UnsetValue</c>)
    /// when off — read by a STATIC <c>Style Selector="Window"</c> in <c>Styles/Typography.axaml</c>
    /// that sets the INHERITED attached property <c>TextElement.FontWeight</c> on every Window. That
    /// Style exists from the moment the stylesheet loads, so every window — cold start's
    /// <c>MainWindow</c> included, and every on-demand window — gets the current value from
    /// construction; no per-window push, no runtime Styles-collection mutation. Explicit Normal when
    /// off is exactly what a descendant would inherit if nothing were set at all, so it is harmless
    /// and sidesteps Attempt 1's UnsetValue trap. Children still behave correctly: untagged text
    /// with no nearer ancestor inherits this value from the Window; Material buttons and RemEx's own
    /// button classes set FontWeight on themselves at Style priority (nearer than the Window), so
    /// their labels are unaffected; themed members and <c>.page-title</c>/<c>.card-title</c> carry
    /// their own setters.
    /// </para>
    /// </summary>
    public const string UntaggedBoldFontWeightKey = "Typo.UntaggedBold.FontWeight";

    public static readonly IReadOnlyList<TypographyMember> Members = new[]
    {
        new TypographyMember("Headline5", TypographySection.Headers, 24, FontWeight.Regular),
        new TypographyMember("Headline6", TypographySection.Headers, 20, FontWeight.Medium),
        new TypographyMember("Subtitle1", TypographySection.Headers, 16, FontWeight.Regular),
        new TypographyMember("PageTitle", TypographySection.Headers, 30, FontWeight.Black),      // App.axaml TextBlock.page-title
        new TypographyMember("CardTitle", TypographySection.Headers, 18, FontWeight.Black),      // App.axaml TextBlock.card-title
        new TypographyMember("Body2", TypographySection.Body, 14, FontWeight.Regular),
        new TypographyMember("PageSubtitle", TypographySection.Body, 12, FontWeight.Medium),     // App.axaml TextBlock.page-subtitle
        new TypographyMember("Caption", TypographySection.Small, 12, FontWeight.Regular),
        new TypographyMember("Overline", TypographySection.Small, 10, FontWeight.Regular),
        new TypographyMember("SensorTitle", TypographySection.Sensor, 12, FontWeight.Bold),      // was CaptionTextBlock + FontWeight="Bold" on the canvas card
        new TypographyMember("SensorMetricName", TypographySection.Sensor, 10, FontWeight.Regular), // was OverlineTextBlock in the dual-metric legend
    };

    /// <summary>The member each section's slider label reports as <c>default → effective</c>.</summary>
    public static readonly IReadOnlyDictionary<TypographySection, string> ReferenceMember = new Dictionary<TypographySection, string>
    {
        [TypographySection.Headers] = "Headline6",
        [TypographySection.Body] = "Body2",
        [TypographySection.Small] = "Caption",
        [TypographySection.Sensor] = "SensorTitle",
    };

    /// <summary>
    /// Which sections receive the halo when the switch is on. All four is the spec's default; the
    /// spec's measured fallback (Task 6, RemEx-jt6w5.6) is <c>{ Headers, Sensor }</c> — change it
    /// here and in <c>TypographyResolverTests.ShadowedSections_IsTheSpecDefault_AllFour</c> together.
    /// </summary>
    public static IReadOnlySet<TypographySection> ShadowedSections { get; } = new HashSet<TypographySection>
    {
        TypographySection.Headers, TypographySection.Body, TypographySection.Small, TypographySection.Sensor,
    };

    public static string ShadowKey(TypographySection section) => $"Typo.{section}.Effect";

    public static TypographyMember Member(string key) => Members.Single(m => m.Key == key);

    public static double ScaleFor(TypographySettings settings, TypographySection section) => section switch
    {
        TypographySection.Headers => settings.HeadersScale,
        TypographySection.Body => settings.BodyScale,
        TypographySection.Small => settings.SmallScale,
        TypographySection.Sensor => settings.SensorScale,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    public static bool BoldFor(TypographySettings settings, TypographySection section) => section switch
    {
        TypographySection.Headers => settings.HeadersBold,
        TypographySection.Body => settings.BodyBold,
        TypographySection.Small => settings.SmallBold,
        TypographySection.Sensor => settings.SensorBold,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, null),
    };

    public static double FontSize(TypographyMember member, TypographySettings settings) =>
        member.BaseSize * TypographySettings.ClampScale(ScaleFor(settings, member.Section));

    /// <summary>
    /// Bold never thins text: a section's Bold switch raises members to at least
    /// <see cref="FontWeight.Bold"/> (700), but a member whose own <see cref="TypographyMember.BaseWeight"/>
    /// is already heavier (e.g. PageTitle/CardTitle at Black, 900) stays at that heavier weight.
    /// Off returns to <see cref="TypographyMember.BaseWeight"/> unchanged.
    /// </summary>
    public static FontWeight FontWeightFor(TypographyMember member, TypographySettings settings) =>
        BoldFor(settings, member.Section)
            ? (FontWeight)Math.Max((int)FontWeight.Bold, (int)member.BaseWeight)
            : member.BaseWeight;

    public static int ReferencePoints(TypographySection section) =>
        (int)Member(ReferenceMember[section]).BaseSize;

    public static int EffectivePoints(TypographySection section, double scale) =>
        (int)Math.Round(Member(ReferenceMember[section]).BaseSize * TypographySettings.ClampScale(scale), MidpointRounding.AwayFromZero);

    /// <summary>Strength 0→100 maps to blur 1→8 px (spec § Text shadow).</summary>
    public static double ShadowBlurRadius(int strength) => 1.0 + 7.0 * Fraction(strength);

    /// <summary>Strength 0→100 maps to opacity 0.35→0.90 (spec § Text shadow).</summary>
    public static double ShadowOpacity(int strength) => 0.35 + 0.55 * Fraction(strength);

    /// <summary>The theme surface, forced opaque: alpha is the effect's Opacity, never the colour's.</summary>
    public static Color ShadowColor(Color surface) => Color.FromRgb(surface.R, surface.G, surface.B);

    private static double Fraction(int strength) =>
        Math.Clamp(strength, TypographySettings.MinShadowStrength, TypographySettings.MaxShadowStrength) / 100.0;

    public static TypographyResolution Resolve(TypographySettings settings, Color surface)
    {
        var s = TypographySettings.Normalize(settings);

        var sizes = new Dictionary<string, double>(Members.Count);
        var weights = new Dictionary<string, FontWeight>(Members.Count + 1);
        foreach (var member in Members)
        {
            sizes[member.FontSizeKey] = FontSize(member, s);
            weights[member.FontWeightKey] = FontWeightFor(member, s);
        }
        // ALWAYS present — see UntaggedBoldFontWeightKey's remarks for why an always-present key
        // with an explicit Normal/Bold value, not an absent-or-Unset one, is the mechanism.
        weights[UntaggedBoldFontWeightKey] = s.BodyBold ? FontWeight.Bold : FontWeight.Normal;

        var shadow = s.ShadowEnabled
            ? new TextShadow(ShadowColor(surface), ShadowBlurRadius(s.ShadowStrength), ShadowOpacity(s.ShadowStrength))
            : null;
        var shadows = new Dictionary<TypographySection, TextShadow?>();
        foreach (var section in Enum.GetValues<TypographySection>())
        {
            shadows[section] = ShadowedSections.Contains(section) ? shadow : null;
        }

        return new TypographyResolution(
            sizes,
            weights,
            shadows,
            DefaultFontSizeBase * s.BodyScale,
            UntaggedBold: s.BodyBold,
            SensorTitleBackdrop: s.SensorTitleBackdrop);
    }
}
