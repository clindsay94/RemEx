// Transcribed from material-components-android 1.14.0: lib/java/com/google/android/material/color/utilities/ToneDeltaPair.java
namespace Remex.Core.Theming.Mcu;

/// <summary>Documents a constraint between two DynamicColors, in which their tones must have a certain distance from each other.</summary>
public sealed class ToneDeltaPair
{
    public DynamicColor RoleA { get; }
    public DynamicColor RoleB { get; }
    public double Delta { get; }
    public TonePolarity Polarity { get; }
    public bool StayTogether { get; }

    public ToneDeltaPair(DynamicColor roleA, DynamicColor roleB, double delta, TonePolarity polarity, bool stayTogether)
    {
        RoleA = roleA;
        RoleB = roleB;
        Delta = delta;
        Polarity = polarity;
        StayTogether = stayTogether;
    }
}
