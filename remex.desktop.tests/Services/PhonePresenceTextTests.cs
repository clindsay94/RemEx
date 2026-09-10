using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// What the shell actually SAYS about attached phones (RemEx-0z7w).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PhonePresence.Evaluate"/> has had 17 tests since RemEx-porg and was consumed by nothing
/// — shipped, correct and dead. These cover the step that was missing between it and a user: turning
/// a status into words, and proving those words exist in every language.
/// </para>
/// <para>
/// SPLIT FROM THE LOOKUP so the choice is testable without a resource system, the same split the
/// Android side settled on in RemEx-ivkq. <see cref="PhonePresence.Describe"/> returns a key and an
/// optional argument; the view model does the lookup and the formatting.
/// </para>
/// </remarks>
public class PhonePresenceTextTests
{
    private static PhonePresenceStatus Status(PhonePresenceState state, int count, string? name = null)
        => new(state, count, name);

    [Fact]
    public void NoPhoneSaysSo_RatherThanFallingBackToTheLoopbackLink()
    {
        // The state the whole feature exists for: the loopback socket is up, and before this the user
        // was told "Connected" with nothing attached.
        var (key, argument) = PhonePresence.Describe(Status(PhonePresenceState.NoPhone, 0));

        Assert.Equal("Shell_NoPhoneConnected", key);
        Assert.Null(argument);
    }

    [Fact]
    public void OneNamedPhoneIsNamed()
    {
        var (key, argument) = PhonePresence.Describe(
            Status(PhonePresenceState.OnePhone, 1, "Galaxy S26 Ultra"));

        Assert.Equal("Shell_PhoneConnectedNamed", key);
        Assert.Equal("Galaxy S26 Ultra", argument);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OneUNNAMEDPhoneGetsADifferentStringRatherThanAHoleInTheNamedOne(string? name)
    {
        // A phone reaches the registry authenticated but not necessarily NAMED — a client id rides on
        // ping and a device name need never arrive. Formatting a blank into "{0} connected" renders
        // " connected", on the one row whose job is to say what is attached.
        var (key, argument) = PhonePresence.Describe(Status(PhonePresenceState.OnePhone, 1, name));

        Assert.Equal("Shell_PhoneConnectedUnnamed", key);
        Assert.Null(argument);
    }

    [Fact]
    public void SeveralPhonesAreCountedAndNotNamed()
    {
        // Naming one of several is arbitrary and reads as though it is the only one — the reason
        // Evaluate only offers FirstDeviceName in the single-phone case.
        var (key, argument) = PhonePresence.Describe(
            Status(PhonePresenceState.SeveralPhones, 3, "Galaxy S26 Ultra"));

        Assert.Equal("Shell_PhonesConnectedSeveral", key);
        Assert.Equal("3", argument);
    }

    [Fact]
    public void EveryKeyDescribeCanReturnExistsInEnglishAndTakesTheArgumentsItIsGiven()
    {
        // THE HALF A MAPPING TEST CANNOT COVER. Describe returning "Shell_PhoneConnectedNamed" proves
        // nothing about whether that key exists — a missing one resolves to empty or to the key text
        // at runtime, silently, on a row nobody looks at twice.
        var english = LoadResx("Strings.resx");

        foreach (var (key, argument) in AllOutcomes())
        {
            Assert.True(english.ContainsKey(key), $"Strings.resx does not declare {key}");
            var value = english[key];

            // A string given an argument must have somewhere to put it, and one given none must not
            // ask for one — a stray {0} renders literally.
            Assert.Equal(argument is not null, value.Contains("{0}"));
        }
    }

    [Theory]
    [InlineData("Strings.es.resx")]
    [InlineData("Strings.fr.resx")]
    [InlineData("Strings.hi.resx")]
    [InlineData("Strings.id.resx")]
    [InlineData("Strings.pl.resx")]
    [InlineData("Strings.pt-BR.resx")]
    [InlineData("Strings.tr.resx")]
    [InlineData("Strings.uk.resx")]
    public void EveryLocaleCarriesTheseKeysWithMatchingPlaceholders(string fileName)
    {
        // check-localization.ps1 covers this repo-wide, but it runs against a baseline that suppresses
        // 178 known findings — so a NEW key landing wrong in one locale is exactly the kind of thing a
        // baselined sweep can be talked into ignoring. These four are pinned directly.
        var english = LoadResx("Strings.resx");
        var localized = LoadResx(fileName);

        foreach (var (key, _) in AllOutcomes())
        {
            Assert.True(localized.ContainsKey(key), $"{fileName} does not declare {key}");
            Assert.Equal(english[key].Contains("{0}"), localized[key].Contains("{0}"));
            Assert.False(string.IsNullOrWhiteSpace(localized[key]), $"{fileName}: {key} is blank");
        }
    }

    /// <summary>Every (key, argument) pair Describe can produce, driven through the real method.</summary>
    /// <remarks>
    /// INPUTS DERIVED FROM THE ENUM, not listed (review). An earlier version drove Describe with four
    /// hardcoded statuses and claimed in this very comment that "a new branch without a string is
    /// caught" — which was false. Adding a state and a branch would have left this array untouched,
    /// both tests below green, and the shell rendering the literal key text, because
    /// LocalizationService's indexer falls back to `?? key`. Enumerating the states means a new
    /// member is exercised whether or not anyone remembers this file.
    /// <para>
    /// OnePhone appears twice on purpose: named and unnamed take different branches for the same
    /// state, and that split is the whole reason a blank device name does not render a hole.
    /// </para>
    /// </remarks>
    private static (string Key, string? Argument)[] AllOutcomes()
    {
        var outcomes = new List<(string, string?)>();
        foreach (var state in Enum.GetValues<PhonePresenceState>())
        {
            var count = state == PhonePresenceState.NoPhone ? 0
                : state == PhonePresenceState.OnePhone ? 1
                : 2;
            outcomes.Add(PhonePresence.Describe(Status(state, count, null)));
            outcomes.Add(PhonePresence.Describe(Status(state, count, "Galaxy S26 Ultra")));
        }
        return [.. outcomes];
    }

    [Fact]
    public void EveryPresenceStateIsDriven_SoANewOneCannotSlipPastTheTestsAbove()
    {
        // The assertion that keeps AllOutcomes honest. Without it, a future edit could narrow the
        // loop back to a literal list and the two tests above would quietly stop covering the new
        // state while still claiming to.
        var distinctKeys = AllOutcomes().Select(o => o.Key).Distinct().Count();

        Assert.True(
            distinctKeys >= Enum.GetValues<PhonePresenceState>().Length,
            $"Describe produced {distinctKeys} distinct keys for "
            + $"{Enum.GetValues<PhonePresenceState>().Length} presence states — a state is sharing a "
            + "key, or AllOutcomes stopped enumerating them");
    }

    private static System.Collections.Generic.Dictionary<string, string> LoadResx(string fileName)
    {
        var path = Path.Combine(RepoRoot(), "remex.desktop", "Localization", fileName);
        Assert.True(File.Exists(path), $"Not found: {path}");

        return XDocument.Load(path).Root!
            .Elements("data")
            .Where(d => d.Attribute("name") is not null)
            .ToDictionary(
                d => d.Attribute("name")!.Value,
                d => d.Element("value")?.Value ?? string.Empty);
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
