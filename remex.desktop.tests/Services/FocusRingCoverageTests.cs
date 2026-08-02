using System.Runtime.CompilerServices;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Pins which control types get a visible keyboard-focus ring (RemEx-2p19).
/// </summary>
/// <remarks>
/// <para>
/// WHY A SOURCE-TEXT TEST RATHER THAN A RENDERED ONE. An Avalonia selector that matches nothing
/// fails SILENTLY — it is not a compile error and not a runtime error, it simply never applies. So
/// a build passing says nothing about whether these styles reach anything, and the only cheap
/// mechanical check available is that the declarations exist at all. That is a weaker guarantee
/// than a screenshot and is deliberately not pretending otherwise: what this catches is a future
/// edit DELETING a control type from the ring, not a selector that was wrong on the day it was
/// written. The four-theme visual pass is still the real verification.
/// </para>
/// <para>
/// The defect being guarded against is specific. Styling Button alone meant a keyboard user tabbing
/// through a form got a clear accent ring on the buttons and Fluent's default everywhere else,
/// which on CyberNOC and Monolith is near-invisible against their dark surfaces. Focus appeared to
/// VANISH when it left a button and reappear when it returned, so the tab order read as broken
/// rather than as under-styled.
/// </para>
/// </remarks>
public class FocusRingCoverageTests
{
    /// <summary>
    /// Every control type a keyboard can land on that this app actually uses.
    /// </summary>
    /// <remarks>
    /// TextBox is the one that matters most: it is the control a user is most likely to be typing
    /// into without knowing they are focused on it.
    /// </remarks>
    public static readonly string[] FocusableControlTypes =
    [
        "Button",
        "ToggleButton",
        "RepeatButton",
        "ListBoxItem",
        "TextBox",
        "ComboBox",
        "CheckBox",
        "RadioButton",
        "ToggleSwitch",
        "Slider"
    ];

    [Theory]
    [MemberData(nameof(ControlTypeCases))]
    public void EveryFocusableControlTypeDeclaresAFocusVisibleRing(string controlType)
    {
        var appAxaml = File.ReadAllText(Path.Combine(RepoRoot, "remex.desktop", "App.axaml"));

        Assert.Contains($"Selector=\"{controlType}:focus-visible", appAxaml);
    }

    public static TheoryData<string> ControlTypeCases()
    {
        var data = new TheoryData<string>();
        foreach (var type in FocusableControlTypes) data.Add(type);
        return data;
    }

    [Fact]
    public void TheRingUsesAThemeTokenRatherThanALiteralColour()
    {
        // A literal would be invisible on at least one of the four themes - the failure just fixed
        // for the accent swatches (RemEx-wcvr), where a white hover border showed nothing on
        // SolarFlare's near-white surface. AccentPrimaryBrush is declared by every theme, so each
        // rings in its own accent.
        var appAxaml = File.ReadAllText(Path.Combine(RepoRoot, "remex.desktop", "App.axaml"));
        var focusRingSection = appAxaml[appAxaml.IndexOf("KEYBOARD-FOCUS VISIBLE RING", StringComparison.Ordinal)..];
        var endOfSection = focusRingSection.IndexOf("Slider:focus-visible", StringComparison.Ordinal);

        var declarations = focusRingSection[..endOfSection];

        Assert.DoesNotContain("Value=\"White\"", declarations);
        Assert.DoesNotContain("Value=\"#", declarations);
        Assert.Contains("AccentPrimaryBrush", declarations);
    }

    [Fact]
    public void TheRingIsFocusVisibleAndNotPlainFocus()
    {
        // :focus-visible fires for keyboard traversal and not for a mouse click. Using :focus would
        // leave a ring behind every click, so the UI would look permanently focused - which is the
        // distinction the pseudo-class exists for.
        var appAxaml = File.ReadAllText(Path.Combine(RepoRoot, "remex.desktop", "App.axaml"));

        foreach (var type in FocusableControlTypes)
        {
            Assert.DoesNotContain($"Selector=\"{type}:focus\"", appAxaml);
        }
    }

    private static string RepoRoot => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile)!, "..", ".."));

    private static string ThisFile => GetThisFilePath();

    private static string GetThisFilePath([CallerFilePath] string path = "") => path;
}
