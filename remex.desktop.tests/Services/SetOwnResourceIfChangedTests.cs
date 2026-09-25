using Avalonia.Controls;
using Avalonia.Media;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Perf audit P2-14: <c>ApplyCustomizationCore</c>'s font/UiScale writes are OWN keys on
/// <c>Application.Resources</c> (can't be batched into the override dictionary the way the ~56
/// palette resources are — see that method's remark), so each was firing its own
/// <c>ResourcesChanged</c> unconditionally on every apply, even a colour/opacity-only slider tick
/// that never touches a font or UiScale setting. <see cref="ThemeService.SetOwnResourceIfChanged"/>
/// is the fix: skip the write when the value is already there.
///
/// Takes a plain <see cref="ResourceDictionary"/>, not <c>Application.Current</c>, specifically so
/// this is testable at all — <c>ApplyCustomizationCore</c>'s own font-writing block never runs
/// under test (guarded on <c>Application.Current</c>, null with no Avalonia.Headless reference in
/// this assembly), so the equality-skip logic has to be exercised directly like this instead.
///
/// PROVEN BY REFERENCE IDENTITY, NOT AN EVENT. <c>IResourceDictionary</c> exposes no public
/// "a value changed" event to subscribe to — the real notification is internal Avalonia visual-tree
/// plumbing this assembly cannot observe without a live Application. Whether the STORED instance is
/// the original or the new one is an equally direct proxy for "was the write skipped": a skip
/// leaves the dictionary holding the exact same reference it already had.
/// </summary>
public class SetOwnResourceIfChangedTests
{
    [Fact]
    public void AnUnsetKey_IsWritten()
    {
        var resources = new ResourceDictionary();

        ThemeService.SetOwnResourceIfChanged(resources, "UiScale", 1.1);

        resources["UiScale"].Should().Be(1.1);
    }

    [Fact]
    public void AnEqualValue_LeavesTheStoredInstanceUntouched()
    {
        object boxed = 1.1; // boxed once so ReferenceEquals actually distinguishes "same" from "equal"
        var resources = new ResourceDictionary { ["UiScale"] = boxed };

        ThemeService.SetOwnResourceIfChanged(resources, "UiScale", 1.1);

        ReferenceEquals(resources["UiScale"], boxed).Should().BeTrue(
            "the value is unchanged, so the write - and whatever notification it would fire - should be skipped entirely");
    }

    [Fact]
    public void ADifferentValue_ReplacesTheStoredInstance()
    {
        var resources = new ResourceDictionary { ["UiScale"] = 1.0 };

        ThemeService.SetOwnResourceIfChanged(resources, "UiScale", 1.1);

        resources["UiScale"].Should().Be(1.1);
    }

    [Fact]
    public void EqualFontFamilies_AreNotRewritten()
    {
        // The real-world case this row is about: SystemFontService.ResolveFontOrDefault resolves a
        // NEW FontFamily instance every apply, even when the string it resolved from didn't change.
        // FontFamily has value equality (Avalonia), which is what makes the skip actually fire for
        // the font keys and not just for primitives like UiScale.
        var first = new FontFamily("avares://Remex.Desktop/Assets/Fonts#Orbitron");
        var second = new FontFamily("avares://Remex.Desktop/Assets/Fonts#Orbitron");
        first.Should().Be(second, "the test below only proves anything if two independently-constructed instances compare equal");

        var resources = new ResourceDictionary { ["PageTitleFontFamily"] = first };

        ThemeService.SetOwnResourceIfChanged(resources, "PageTitleFontFamily", second);

        ReferenceEquals(resources["PageTitleFontFamily"], first).Should().BeTrue(
            "a different FontFamily instance with the same value must not replace what's already there");
    }
}
