using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Core.Models;
using Xunit;

namespace Remex.Desktop.Tests.ViewModels;

/// <summary>
/// <c>CustomizationViewModel.ApplyAndSave</c> must write back every field of
/// <see cref="CustomizationSettings"/>, not just the ones its sliders bind to.
/// </summary>
/// <remarks>
/// <para>
/// THE FAILURE THIS CATCHES IS DELETION BY OMISSION, and it is completely silent. ApplyAndSave
/// builds a BRAND NEW record rather than editing the loaded one, so any property it forgets is not
/// left alone — it is reset to its C# default and then saved over the user's value. ThemeContrast
/// lived that way from the day it was added: persisted correctly, reloaded correctly, and wiped the
/// next time the user moved any unrelated slider. Half of "the contrast setting does nothing"
/// (RemEx-68ynp) was this, and no test could see it because nothing ever failed.
/// </para>
/// <para>
/// REFLECTION OVER THE RECORD RATHER THAN A HARDCODED LIST, so that a field added next year is
/// covered on the day it is added. That is the whole value: the bug is not writing this code wrong,
/// it is writing the record right and forgetting this file exists.
/// </para>
/// </remarks>
public class CustomizationSettingsRoundTripTests
{
    [Fact]
    public void ApplyAndSaveAssignsEveryPersistedCustomizationField()
    {
        var initializer = ApplyAndSaveInitializer();

        var missing = PersistedProperties()
            .Where(name => !Regex.IsMatch(initializer, $@"(?<![\w.]){Regex.Escape(name)}\s*="))
            .ToArray();

        missing.Should().BeEmpty(
            "ApplyAndSave constructs a new CustomizationSettings, so any field it does not assign is "
            + "silently reset to its default and written over the user's saved value — carry it "
            + "forward from the loaded profile the way ThemeContrast and CardHeaderFontFamily are");
    }

    [Fact]
    public void TheAliasPropertiesAreExcludedRatherThanQuietlyPassing()
    {
        // ANTI-VACUITY, AND A REAL DISTINCTION. BaseTheme and CanvasBackgroundType are [JsonIgnore]
        // façades over ThemeId and BackgroundMaterial; demanding both halves of an alias pair would
        // be demanding the same value be assigned twice. Excluding them is correct — but only as
        // long as the exclusion is narrow, so this pins exactly which names it removes and proves
        // the filter still lets the real fields through.
        var persisted = PersistedProperties();

        persisted.Should().NotContain("BaseTheme");
        persisted.Should().NotContain("CanvasBackgroundType");
        persisted.Should().Contain("ThemeId");
        persisted.Should().Contain("BackgroundMaterial");
        persisted.Should().Contain("ThemeContrast");
        persisted.Should().Contain("UseLightPalette");
        persisted.Length.Should().BeGreaterThan(15,
            "if reflection stops finding the record's properties this class asserts nothing");
    }

    /// <summary>Every settable property that actually round-trips to disk.</summary>
    private static string[] PersistedProperties() =>
        typeof(CustomizationSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.Name)
            .ToArray();

    /// <summary>The object-initializer body of <c>ApplyAndSave</c>, as source text.</summary>
    private static string ApplyAndSaveInitializer()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(),
            "remex.desktop", "ViewModels", "CustomizationViewModel.cs"));

        var match = Regex.Match(source,
            @"var settings = new CustomizationSettings\s*\{(.*?)\n        \};",
            RegexOptions.Singleline);

        match.Success.Should().BeTrue(
            "ApplyAndSave's initializer moved or was reshaped — re-point this test rather than deleting it");
        return match.Groups[1].Value;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
