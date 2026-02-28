using System.Collections.Generic;

namespace Remex.Core.Models;

/// <summary>
/// Per-card color scheme with individually customizable elements.
/// </summary>
public record SensorCardTheme
{
    public string Name { get; init; } = "Default";
    public string CardBackground { get; init; } = "#0A0A16";
    public string AccentColor { get; init; } = "#C0C0FF";
    public string ValueColor { get; init; } = "#E0E0FF";
    public string LabelColor { get; init; } = "#6666AA";
    public string UnitColor { get; init; } = "#8888AA";

    /// <summary>
    /// Returns a deep copy with a new name.
    /// </summary>
    public SensorCardTheme WithName(string name) => this with { Name = name };

    /// <summary>
    /// Prebuilt material-design-inspired theme presets.
    /// </summary>
    public static IReadOnlyList<SensorCardTheme> Presets { get; } = new[]
    {
        new SensorCardTheme
        {
            Name = "Default",
            CardBackground = "#0A0A16",
            AccentColor = "#C0C0FF",
            ValueColor = "#E0E0FF",
            LabelColor = "#6666AA",
            UnitColor = "#8888AA",
        },
        new SensorCardTheme
        {
            Name = "Magenta & Cyan",
            CardBackground = "#0D0D1A",
            AccentColor = "#00E5FF",
            ValueColor = "#FF4081",
            LabelColor = "#00BCD4",
            UnitColor = "#80DEEA",
        },
        new SensorCardTheme
        {
            Name = "Smoke & Gold",
            CardBackground = "#1A1A1A",
            AccentColor = "#FFD700",
            ValueColor = "#FFC107",
            LabelColor = "#9E9E9E",
            UnitColor = "#BDBDBD",
        },
        new SensorCardTheme
        {
            Name = "Emerald Night",
            CardBackground = "#0A1A0F",
            AccentColor = "#00E676",
            ValueColor = "#69F0AE",
            LabelColor = "#2E7D32",
            UnitColor = "#81C784",
        },
        new SensorCardTheme
        {
            Name = "Sunset",
            CardBackground = "#1A0A0A",
            AccentColor = "#FF6D00",
            ValueColor = "#FF9E80",
            LabelColor = "#BF360C",
            UnitColor = "#FFAB91",
        },
        new SensorCardTheme
        {
            Name = "Arctic",
            CardBackground = "#0A0F1A",
            AccentColor = "#448AFF",
            ValueColor = "#82B1FF",
            LabelColor = "#1565C0",
            UnitColor = "#90CAF9",
        },
        new SensorCardTheme
        {
            Name = "Neon Purple",
            CardBackground = "#120A1A",
            AccentColor = "#E040FB",
            ValueColor = "#EA80FC",
            LabelColor = "#7B1FA2",
            UnitColor = "#CE93D8",
        },
        new SensorCardTheme
        {
            Name = "Monochrome",
            CardBackground = "#121212",
            AccentColor = "#FFFFFF",
            ValueColor = "#E0E0E0",
            LabelColor = "#757575",
            UnitColor = "#9E9E9E",
        },
    };
}
