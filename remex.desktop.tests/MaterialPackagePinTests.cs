using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// The Material.Avalonia package set is held on the last line that still targets Avalonia 11.x
/// (RemEx-851li).
/// </summary>
/// <remarks>
/// <para>
/// WHAT ACTUALLY HAPPENS ON A BUMP, MEASURED RATHER THAN GUESSED. Material.Avalonia 3.15.0 and later
/// declare a dependency on Avalonia 12.0.0, and this solution is on 11.3.11. Setting the pins to
/// 3.15.0 fails <c>dotnet restore</c> outright:
/// <code>
/// error NU1605: Detected package downgrade: Avalonia from 12.0.0 to 11.3.11.
///   Remex.Desktop -> Material.Avalonia 3.15.0 -> Avalonia (>= 12.0.0)
///   Remex.Desktop -> Avalonia (>= 11.3.11)
/// </code>
/// So the ceiling is enforced by NuGet and a careless bump cannot pass unnoticed. The first draft of
/// this file assumed the opposite — that a bump would slip through quietly — and asserted against a
/// hazard nobody had measured. Review asked for the measurement and it came back the other way.
/// </para>
/// <para>
/// WHICH RAISES THE FAIR QUESTION OF WHY THIS FILE EXISTS. Because NU1605 names a *downgrade*, which
/// reads as "your Avalonia is too old — raise it", and the correct response here is the opposite:
/// the Material line is capped on purpose until the operator decides to move the framework. These
/// assertions fail first, in the test output, saying so in words. They are a signpost in front of a
/// wall, not the wall.
/// </para>
/// <para>
/// PARSED, NOT REGEXED (review). An earlier version matched raw file text and took the first hit,
/// which cannot tell a real element from one quoted inside a comment — and quoting elements in
/// comments is exactly this repo's house style, including in the very files this reads. A stale
/// commented-out version above the real entry would have made these assertions read the comment:
/// failing on a correct file in one order, and passing on an Avalonia 12 app in the other. XDocument
/// ignores comment nodes.
/// </para>
/// <para>
/// It does not consult nuget.org. A suite that depends on the network and on a third party's release
/// schedule fails for reasons nobody can act on, and people learn to ignore it.
/// </para>
/// </remarks>
public class MaterialPackagePinTests
{
    /// <summary>
    /// Package, pinned version, and the first version that would require Avalonia 12.
    /// </summary>
    /// <remarks>
    /// Verified against the nuspecs on nuget.org on 2026-08-20, and the 3.15.0 boundary re-confirmed
    /// locally by the NU1605 above. <c>FirstAvalonia12</c> feeds the failure message only.
    /// </remarks>
    private static readonly (string Package, string Pinned, string FirstAvalonia12)[] Pins =
    [
        ("Material.Avalonia", "3.14.2", "3.15.0"),
        ("Material.Avalonia.Dialogs", "3.14.2", "3.15.0"),
        ("Material.Icons.Avalonia", "3.0.0", "3.0.1"),
    ];

    [Fact]
    public void TheMaterialPackagesAreStillOnTheLastAvaloniaElevenLine()
    {
        foreach (var (package, pinned, firstAvalonia12) in Pins)
        {
            PinnedVersionOf(package).Should().Be(pinned,
                $"{package} {firstAvalonia12} and later require Avalonia 12.0.0, and this solution is "
                + "on Avalonia 11.3.11 — restore fails with NU1605 if you raise it. That error calls "
                + "it a downgrade; it is really a deliberate ceiling. If Avalonia has genuinely moved "
                + "to 12, change this test on purpose");
        }
    }

    [Fact]
    public void AvaloniaItselfIsStillOnEleven()
    {
        // The other half of the same claim. If someone upgrades Avalonia to 12 without touching the
        // Material pins, the test above starts guarding the wrong thing — holding Material on an
        // 11-only line under an Avalonia 12 app.
        PinnedVersionOf("Avalonia").Should().StartWith("11.",
            "the Material pins above exist only because this solution is on Avalonia 11. If Avalonia "
            + "has moved to 12, revisit them together rather than leaving them stale");
    }

    [Fact]
    public void TheMaterialPackagesAreReferencedWithoutAVersion()
    {
        // ManagePackageVersionsCentrally is true, so a Version on the PackageReference is NU1008 and
        // fails restore. Measured on RemEx-851li: "The following PackageReference items cannot define
        // a value for Version".
        var project = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "remex.desktop.csproj"));

        foreach (var (package, _, _) in Pins)
        {
            var reference = project.Descendants("PackageReference")
                .SingleOrDefault(e => (string?)e.Attribute("Include") == package);

            reference.Should().NotBeNull(
                $"{package} must still be referenced by remex.desktop — it was added by RemEx-851li "
                + "for the Material overhaul, and a reference that quietly disappears takes the whole "
                + "design system with it");

            // BOTH SPELLINGS. The attribute form is what NU1008 catches; the child-element form
            // <PackageReference Include="X"><Version>..</Version></PackageReference> is equally
            // invalid under CPM and a text search for Version=" would miss it entirely (review).
            reference!.Attribute("Version").Should().BeNull(
                $"{package} must be referenced versionlessly under central package management");
            reference.Element("Version").Should().BeNull(
                $"{package} must not carry a Version child element either");
        }
    }

    /// <summary>The version a <c>PackageVersion</c> element pins, ignoring anything in a comment.</summary>
    private static string PinnedVersionOf(string package)
    {
        var element = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .SingleOrDefault(e => (string?)e.Attribute("Include") == package);

        element.Should().NotBeNull($"{package} should still be pinned in Directory.Packages.props");

        var version = (string?)element!.Attribute("Version");
        version.Should().NotBeNull($"{package} has a PackageVersion entry with no Version attribute");
        return version!;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, ".."));
}
