using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>RemEx-4kv0g.12: every Compose-exposed M3 role is published under Palette&lt;Role&gt; / Palette&lt;Role&gt;Brush from the seed (spec § 2 — unused until spec B binds them).</summary>
public class MaterialRoleKeysTests
{
    internal static readonly string[] ComposeRoles =
    {
        "Primary", "OnPrimary", "PrimaryContainer", "OnPrimaryContainer", "InversePrimary", "Secondary", "OnSecondary", "SecondaryContainer", "OnSecondaryContainer",
        "Tertiary", "OnTertiary", "TertiaryContainer", "OnTertiaryContainer", "Background", "OnBackground", "Surface", "OnSurface", "SurfaceVariant", "OnSurfaceVariant",
        "SurfaceTint", "InverseSurface", "InverseOnSurface", "Error", "OnError", "ErrorContainer", "OnErrorContainer", "Outline", "OutlineVariant", "Scrim",
        "SurfaceBright", "SurfaceDim", "SurfaceContainer", "SurfaceContainerHigh", "SurfaceContainerHighest", "SurfaceContainerLow", "SurfaceContainerLowest",
        "PrimaryFixed", "PrimaryFixedDim", "OnPrimaryFixed", "OnPrimaryFixedVariant", "SecondaryFixed", "SecondaryFixedDim", "OnSecondaryFixed", "OnSecondaryFixedVariant",
        "TertiaryFixed", "TertiaryFixedDim", "OnTertiaryFixed", "OnTertiaryFixedVariant",
    };

    [Fact]
    public void ThemeServicePublishesEveryComposeRoleAsAPaletteKeyAndBrush()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "Services", "ThemeService.cs"));
        var published = Regex.Matches(source, @"SetResourceOverrideInternal\(""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        published.Should().NotBeEmpty("the scan must see the literal keys — a loop with interpolated names is invisible to ThemeKeyCoverageTests too");
        ComposeRoles.Should().HaveCount(48);
        foreach (var role in ComposeRoles)
        {
            published.Should().Contain($"Palette{role}", $"role {role} must be published as a Color");
            published.Should().Contain($"Palette{role}Brush", $"role {role} must be published as a SolidColorBrush");
        }
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
