namespace Remex.Core.Theming.Mcu;

/// <summary>Every MaterialDynamicColors role of one DynamicScheme as opaque ARGB — the record the PC paints from and the vectors are checked against.</summary>
public sealed class MaterialRoles
{
    private static readonly MaterialDynamicColors Colors = new();

    /// <summary>The 62 role names, camelCase, in MaterialDynamicColors' declaration order — the same list as mcu-vectors.json "roles".</summary>
    public static readonly string[] RoleNames =
    {
        "primaryPaletteKeyColor", "secondaryPaletteKeyColor", "tertiaryPaletteKeyColor", "neutralPaletteKeyColor", "neutralVariantPaletteKeyColor",
        "background", "onBackground", "surface", "surfaceDim", "surfaceBright", "surfaceContainerLowest", "surfaceContainerLow", "surfaceContainer",
        "surfaceContainerHigh", "surfaceContainerHighest", "onSurface", "surfaceVariant", "onSurfaceVariant", "inverseSurface", "inverseOnSurface",
        "outline", "outlineVariant", "shadow", "scrim", "surfaceTint", "primary", "onPrimary", "primaryContainer", "onPrimaryContainer", "inversePrimary",
        "secondary", "onSecondary", "secondaryContainer", "onSecondaryContainer", "tertiary", "onTertiary", "tertiaryContainer", "onTertiaryContainer",
        "error", "onError", "errorContainer", "onErrorContainer", "primaryFixed", "primaryFixedDim", "onPrimaryFixed", "onPrimaryFixedVariant",
        "secondaryFixed", "secondaryFixedDim", "onSecondaryFixed", "onSecondaryFixedVariant", "tertiaryFixed", "tertiaryFixedDim", "onTertiaryFixed",
        "onTertiaryFixedVariant", "controlActivated", "controlNormal", "controlHighlight", "textPrimaryInverse", "textSecondaryAndTertiaryInverse",
        "textPrimaryInverseDisableOnly", "textSecondaryAndTertiaryInverseDisabled", "textHintInverse",
    };

    public uint PrimaryPaletteKeyColor { get; init; }
    public uint SecondaryPaletteKeyColor { get; init; }
    public uint TertiaryPaletteKeyColor { get; init; }
    public uint NeutralPaletteKeyColor { get; init; }
    public uint NeutralVariantPaletteKeyColor { get; init; }
    public uint Background { get; init; }
    public uint OnBackground { get; init; }
    public uint Surface { get; init; }
    public uint SurfaceDim { get; init; }
    public uint SurfaceBright { get; init; }
    public uint SurfaceContainerLowest { get; init; }
    public uint SurfaceContainerLow { get; init; }
    public uint SurfaceContainer { get; init; }
    public uint SurfaceContainerHigh { get; init; }
    public uint SurfaceContainerHighest { get; init; }
    public uint OnSurface { get; init; }
    public uint SurfaceVariant { get; init; }
    public uint OnSurfaceVariant { get; init; }
    public uint InverseSurface { get; init; }
    public uint InverseOnSurface { get; init; }
    public uint Outline { get; init; }
    public uint OutlineVariant { get; init; }
    public uint Shadow { get; init; }
    public uint Scrim { get; init; }
    public uint SurfaceTint { get; init; }
    public uint Primary { get; init; }
    public uint OnPrimary { get; init; }
    public uint PrimaryContainer { get; init; }
    public uint OnPrimaryContainer { get; init; }
    public uint InversePrimary { get; init; }
    public uint Secondary { get; init; }
    public uint OnSecondary { get; init; }
    public uint SecondaryContainer { get; init; }
    public uint OnSecondaryContainer { get; init; }
    public uint Tertiary { get; init; }
    public uint OnTertiary { get; init; }
    public uint TertiaryContainer { get; init; }
    public uint OnTertiaryContainer { get; init; }
    public uint Error { get; init; }
    public uint OnError { get; init; }
    public uint ErrorContainer { get; init; }
    public uint OnErrorContainer { get; init; }
    public uint PrimaryFixed { get; init; }
    public uint PrimaryFixedDim { get; init; }
    public uint OnPrimaryFixed { get; init; }
    public uint OnPrimaryFixedVariant { get; init; }
    public uint SecondaryFixed { get; init; }
    public uint SecondaryFixedDim { get; init; }
    public uint OnSecondaryFixed { get; init; }
    public uint OnSecondaryFixedVariant { get; init; }
    public uint TertiaryFixed { get; init; }
    public uint TertiaryFixedDim { get; init; }
    public uint OnTertiaryFixed { get; init; }
    public uint OnTertiaryFixedVariant { get; init; }
    public uint ControlActivated { get; init; }
    public uint ControlNormal { get; init; }
    public uint ControlHighlight { get; init; }
    public uint TextPrimaryInverse { get; init; }
    public uint TextSecondaryAndTertiaryInverse { get; init; }
    public uint TextPrimaryInverseDisableOnly { get; init; }
    public uint TextSecondaryAndTertiaryInverseDisabled { get; init; }
    public uint TextHintInverse { get; init; }

    public uint this[string roleName] => roleName switch
    {
        "primaryPaletteKeyColor" => PrimaryPaletteKeyColor,
        "secondaryPaletteKeyColor" => SecondaryPaletteKeyColor,
        "tertiaryPaletteKeyColor" => TertiaryPaletteKeyColor,
        "neutralPaletteKeyColor" => NeutralPaletteKeyColor,
        "neutralVariantPaletteKeyColor" => NeutralVariantPaletteKeyColor,
        "background" => Background,
        "onBackground" => OnBackground,
        "surface" => Surface,
        "surfaceDim" => SurfaceDim,
        "surfaceBright" => SurfaceBright,
        "surfaceContainerLowest" => SurfaceContainerLowest,
        "surfaceContainerLow" => SurfaceContainerLow,
        "surfaceContainer" => SurfaceContainer,
        "surfaceContainerHigh" => SurfaceContainerHigh,
        "surfaceContainerHighest" => SurfaceContainerHighest,
        "onSurface" => OnSurface,
        "surfaceVariant" => SurfaceVariant,
        "onSurfaceVariant" => OnSurfaceVariant,
        "inverseSurface" => InverseSurface,
        "inverseOnSurface" => InverseOnSurface,
        "outline" => Outline,
        "outlineVariant" => OutlineVariant,
        "shadow" => Shadow,
        "scrim" => Scrim,
        "surfaceTint" => SurfaceTint,
        "primary" => Primary,
        "onPrimary" => OnPrimary,
        "primaryContainer" => PrimaryContainer,
        "onPrimaryContainer" => OnPrimaryContainer,
        "inversePrimary" => InversePrimary,
        "secondary" => Secondary,
        "onSecondary" => OnSecondary,
        "secondaryContainer" => SecondaryContainer,
        "onSecondaryContainer" => OnSecondaryContainer,
        "tertiary" => Tertiary,
        "onTertiary" => OnTertiary,
        "tertiaryContainer" => TertiaryContainer,
        "onTertiaryContainer" => OnTertiaryContainer,
        "error" => Error,
        "onError" => OnError,
        "errorContainer" => ErrorContainer,
        "onErrorContainer" => OnErrorContainer,
        "primaryFixed" => PrimaryFixed,
        "primaryFixedDim" => PrimaryFixedDim,
        "onPrimaryFixed" => OnPrimaryFixed,
        "onPrimaryFixedVariant" => OnPrimaryFixedVariant,
        "secondaryFixed" => SecondaryFixed,
        "secondaryFixedDim" => SecondaryFixedDim,
        "onSecondaryFixed" => OnSecondaryFixed,
        "onSecondaryFixedVariant" => OnSecondaryFixedVariant,
        "tertiaryFixed" => TertiaryFixed,
        "tertiaryFixedDim" => TertiaryFixedDim,
        "onTertiaryFixed" => OnTertiaryFixed,
        "onTertiaryFixedVariant" => OnTertiaryFixedVariant,
        "controlActivated" => ControlActivated,
        "controlNormal" => ControlNormal,
        "controlHighlight" => ControlHighlight,
        "textPrimaryInverse" => TextPrimaryInverse,
        "textSecondaryAndTertiaryInverse" => TextSecondaryAndTertiaryInverse,
        "textPrimaryInverseDisableOnly" => TextPrimaryInverseDisableOnly,
        "textSecondaryAndTertiaryInverseDisabled" => TextSecondaryAndTertiaryInverseDisabled,
        "textHintInverse" => TextHintInverse,
        _ => throw new ArgumentException($"not a MaterialDynamicColors role: {roleName}", nameof(roleName)),
    };

    public static MaterialRoles From(DynamicScheme scheme) => new()
    {
        PrimaryPaletteKeyColor = Colors.PrimaryPaletteKeyColor().GetArgb(scheme),
        SecondaryPaletteKeyColor = Colors.SecondaryPaletteKeyColor().GetArgb(scheme),
        TertiaryPaletteKeyColor = Colors.TertiaryPaletteKeyColor().GetArgb(scheme),
        NeutralPaletteKeyColor = Colors.NeutralPaletteKeyColor().GetArgb(scheme),
        NeutralVariantPaletteKeyColor = Colors.NeutralVariantPaletteKeyColor().GetArgb(scheme),
        Background = Colors.Background().GetArgb(scheme),
        OnBackground = Colors.OnBackground().GetArgb(scheme),
        Surface = Colors.Surface().GetArgb(scheme),
        SurfaceDim = Colors.SurfaceDim().GetArgb(scheme),
        SurfaceBright = Colors.SurfaceBright().GetArgb(scheme),
        SurfaceContainerLowest = Colors.SurfaceContainerLowest().GetArgb(scheme),
        SurfaceContainerLow = Colors.SurfaceContainerLow().GetArgb(scheme),
        SurfaceContainer = Colors.SurfaceContainer().GetArgb(scheme),
        SurfaceContainerHigh = Colors.SurfaceContainerHigh().GetArgb(scheme),
        SurfaceContainerHighest = Colors.SurfaceContainerHighest().GetArgb(scheme),
        OnSurface = Colors.OnSurface().GetArgb(scheme),
        SurfaceVariant = Colors.SurfaceVariant().GetArgb(scheme),
        OnSurfaceVariant = Colors.OnSurfaceVariant().GetArgb(scheme),
        InverseSurface = Colors.InverseSurface().GetArgb(scheme),
        InverseOnSurface = Colors.InverseOnSurface().GetArgb(scheme),
        Outline = Colors.Outline().GetArgb(scheme),
        OutlineVariant = Colors.OutlineVariant().GetArgb(scheme),
        Shadow = Colors.Shadow().GetArgb(scheme),
        Scrim = Colors.Scrim().GetArgb(scheme),
        SurfaceTint = Colors.SurfaceTint().GetArgb(scheme),
        Primary = Colors.Primary().GetArgb(scheme),
        OnPrimary = Colors.OnPrimary().GetArgb(scheme),
        PrimaryContainer = Colors.PrimaryContainer().GetArgb(scheme),
        OnPrimaryContainer = Colors.OnPrimaryContainer().GetArgb(scheme),
        InversePrimary = Colors.InversePrimary().GetArgb(scheme),
        Secondary = Colors.Secondary().GetArgb(scheme),
        OnSecondary = Colors.OnSecondary().GetArgb(scheme),
        SecondaryContainer = Colors.SecondaryContainer().GetArgb(scheme),
        OnSecondaryContainer = Colors.OnSecondaryContainer().GetArgb(scheme),
        Tertiary = Colors.Tertiary().GetArgb(scheme),
        OnTertiary = Colors.OnTertiary().GetArgb(scheme),
        TertiaryContainer = Colors.TertiaryContainer().GetArgb(scheme),
        OnTertiaryContainer = Colors.OnTertiaryContainer().GetArgb(scheme),
        Error = Colors.Error().GetArgb(scheme),
        OnError = Colors.OnError().GetArgb(scheme),
        ErrorContainer = Colors.ErrorContainer().GetArgb(scheme),
        OnErrorContainer = Colors.OnErrorContainer().GetArgb(scheme),
        PrimaryFixed = Colors.PrimaryFixed().GetArgb(scheme),
        PrimaryFixedDim = Colors.PrimaryFixedDim().GetArgb(scheme),
        OnPrimaryFixed = Colors.OnPrimaryFixed().GetArgb(scheme),
        OnPrimaryFixedVariant = Colors.OnPrimaryFixedVariant().GetArgb(scheme),
        SecondaryFixed = Colors.SecondaryFixed().GetArgb(scheme),
        SecondaryFixedDim = Colors.SecondaryFixedDim().GetArgb(scheme),
        OnSecondaryFixed = Colors.OnSecondaryFixed().GetArgb(scheme),
        OnSecondaryFixedVariant = Colors.OnSecondaryFixedVariant().GetArgb(scheme),
        TertiaryFixed = Colors.TertiaryFixed().GetArgb(scheme),
        TertiaryFixedDim = Colors.TertiaryFixedDim().GetArgb(scheme),
        OnTertiaryFixed = Colors.OnTertiaryFixed().GetArgb(scheme),
        OnTertiaryFixedVariant = Colors.OnTertiaryFixedVariant().GetArgb(scheme),
        ControlActivated = Colors.ControlActivated().GetArgb(scheme),
        ControlNormal = Colors.ControlNormal().GetArgb(scheme),
        ControlHighlight = Colors.ControlHighlight().GetArgb(scheme),
        TextPrimaryInverse = Colors.TextPrimaryInverse().GetArgb(scheme),
        TextSecondaryAndTertiaryInverse = Colors.TextSecondaryAndTertiaryInverse().GetArgb(scheme),
        TextPrimaryInverseDisableOnly = Colors.TextPrimaryInverseDisableOnly().GetArgb(scheme),
        TextSecondaryAndTertiaryInverseDisabled = Colors.TextSecondaryAndTertiaryInverseDisabled().GetArgb(scheme),
        TextHintInverse = Colors.TextHintInverse().GetArgb(scheme),
    };
}
