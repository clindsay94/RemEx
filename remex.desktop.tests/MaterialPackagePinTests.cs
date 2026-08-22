using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests;

/// <summary>
/// The Material.Avalonia package set and Avalonia itself must stay on generations that can actually
/// resolve together (RemEx-851li, rewritten by RemEx-jcma3).
/// </summary>
/// <remarks>
/// <para>
/// THIS FILE USED TO GUARD THE OPPOSITE CLAIM, and that is worth saying plainly rather than quietly
/// rewriting. Until 2026-08-21 the solution was on Avalonia 11.3.11 and these tests asserted that
/// the Material packages were held at the last versions targeting Avalonia 11 — 3.14.2 and 3.0.0 —
/// because 3.15.0 and 3.0.1 require Avalonia 12.0.0 and restore refused the pairing:
/// <code>
/// error NU1605: Detected package downgrade: Avalonia from 12.0.0 to 11.3.11.
///   Remex.Desktop -> Material.Avalonia 3.15.0 -> Avalonia (>= 12.0.0)
///   Remex.Desktop -> Avalonia (>= 11.3.11)
/// </code>
/// The old test said, in its own failure message, "if Avalonia has genuinely moved to 12, change
/// this test on purpose". Avalonia has moved to 12, so this is that change, made on purpose.
/// </para>
/// <para>
/// WHAT SURVIVES IS THE PAIRING, NOT THE CEILING. The ceiling was a consequence of the framework
/// version, and it is gone. What is still true — and still worth a test — is that these two sides
/// are coupled: Material.Avalonia 3.15.0+ and Material.Icons.Avalonia 3.0.1+ REQUIRE Avalonia 12,
/// and the older lines cannot use it. Either half moving alone recreates the same NU1605, in one
/// direction or the other. So the assertions below check the RELATIONSHIP rather than three frozen
/// version literals, which means they keep working through ordinary patch bumps and only speak up
/// when someone splits the pair.
/// </para>
/// <para>
/// WHY BOTHER, GIVEN RESTORE ALREADY FAILS. Same reason as before: NU1605 names a *downgrade*, which
/// reads as "your Avalonia is too old, raise it". When the real cause is that somebody pinned
/// Material back down, that message points at the wrong file. These assertions fail first, in the
/// test output, and say which half moved. They are a signpost in front of a wall, not the wall.
/// </para>
/// <para>
/// PARSED, NOT REGEXED (review, and still true). An earlier version matched raw file text and took
/// the first hit, which cannot tell a real element from one quoted inside a comment — and quoting
/// elements in comments is exactly this repo's house style, including in the files this reads. A
/// stale commented-out version above the real entry would have made these assertions read the
/// comment. <see cref="XDocument"/> ignores comment nodes.
/// </para>
/// <para>
/// It does not consult nuget.org. A suite that depends on the network and on a third party's release
/// schedule fails for reasons nobody can act on, and people learn to ignore it.
/// </para>
/// </remarks>
public class MaterialPackagePinTests
{
    /// <summary>
    /// Each Material package, and the first version of it that requires Avalonia 12.
    /// </summary>
    /// <remarks>
    /// Verified against the nuspecs on nuget.org on 2026-08-20 and re-confirmed on 2026-08-21 when
    /// the upgrade landed. These boundaries are historical facts about published packages, so unlike
    /// the old pinned-version column they do not go stale.
    /// </remarks>
    private static readonly (string Package, string FirstAvalonia12)[] MaterialPackages =
    [
        ("Material.Avalonia", "3.15.0"),
        ("Material.Avalonia.Dialogs", "3.15.0"),
        ("Material.Icons.Avalonia", "3.0.1"),
    ];

    [Fact]
    public void AvaloniaIsOnTwelve()
    {
        // The anchor the two tests below hang off. Stated as its own assertion so that a framework
        // downgrade fails HERE, with this message, instead of showing up as three confusing
        // complaints about Material packages being too new.
        PinnedVersionOf("Avalonia").Should().StartWith("12.",
            "the Material package versions below require Avalonia 12. If the framework is being "
            + "moved off 12, the Material pins have to move with it — see RemEx-jcma3 for what the "
            + "12 upgrade touched, because reverting it is not just a version edit");
    }

    [Fact]
    public void TheMaterialPackagesAreOnLinesThatRequireAvaloniaTwelve()
    {
        foreach (var (package, firstAvalonia12) in MaterialPackages)
        {
            var pinned = PinnedVersionOf(package);

            IsAtLeast(pinned, firstAvalonia12).Should().BeTrue(
                $"{package} {pinned} predates {firstAvalonia12}, which is the first version built "
                + "against Avalonia 12. This solution is on Avalonia 12, so an older Material line "
                + "is the stale half of a pair — raise it rather than lowering the framework");
        }
    }

    [Fact]
    public void TheAvaloniaPackagesAllMoveTogether()
    {
        // A framework where one package lags the rest resolves, runs, and then fails somewhere
        // specific and unhelpful. Nothing warns about it, so pin the agreement.
        var avalonia = PinnedVersionOf("Avalonia");

        foreach (var package in new[]
                 {
                     "Avalonia.Desktop", "Avalonia.Themes.Fluent", "Avalonia.Fonts.Inter",
                     "Avalonia.Skia", "Avalonia.HarfBuzz",
                 })
        {
            PinnedVersionOf(package).Should().Be(avalonia,
                $"{package} ships from the Avalonia repo on the same cadence as Avalonia itself, so "
                + "a version that differs is drift rather than a decision");
        }
    }

    [Fact]
    public void HarfBuzzIsReferencedBecauseTwelveNoLongerImpliesIt()
    {
        // THE ONE THAT FAILS SILENTLY IN PRODUCTION. Up to Avalonia 11, UseSkia() brought HarfBuzz
        // with it; in 12 it does not. Without the package AND the .UseHarfBuzz() call, Latin text
        // renders perfectly and complex scripts lose their shaping — and RemEx ships Hindi. Nothing
        // throws, so there is no other way to notice. Pin both halves.
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Packages.props"));
        props.Descendants("PackageVersion")
            .Any(e => (string?)e.Attribute("Include") == "Avalonia.HarfBuzz")
            .Should().BeTrue("Avalonia 12's Skia backend does not imply HarfBuzz any more");

        // COMMENTS STRIPPED FIRST, and this file learned that the hard way. The first version of
        // this assertion read the raw source, and the defect injection that was supposed to prove it
        // — commenting the call out as "//.UseHarfBuzz()" — left the test GREEN, because a commented
        // call still contains the string. The call site is explained in prose directly above it too,
        // so even deleting the line outright would have left the words nearby. Same trap
        // BuildIdTests documents, walked into anyway.
        var program = StripLineComments(
            File.ReadAllText(Path.Combine(RepoRoot(), "remex.agent", "Program.cs")));

        program.Should().Contain(".UseHarfBuzz()",
            "referencing the package does nothing on its own — the AppBuilder has to call it");
    }

    /// <summary>Removes <c>//</c> line comments so an assertion cannot be satisfied by prose.</summary>
    /// <remarks>
    /// Deliberately crude: it does not understand strings containing "//", which is fine for the one
    /// file it reads. A smarter version would be a C# parser, and the point here is only to stop a
    /// commented-out call from impersonating a live one.
    /// </remarks>
    private static string StripLineComments(string source)
        => Regex.Replace(source, @"//.*$", string.Empty, RegexOptions.Multiline);

    [Fact]
    public void TheMaterialPackagesAreReferencedWithoutAVersion()
    {
        // ManagePackageVersionsCentrally is true, so a Version on the PackageReference is NU1008 and
        // fails restore. Measured on RemEx-851li: "The following PackageReference items cannot define
        // a value for Version".
        var project = XDocument.Load(Path.Combine(RepoRoot(), "remex.desktop", "remex.desktop.csproj"));

        foreach (var (package, _) in MaterialPackages)
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

    /// <summary>
    /// Whether <paramref name="candidate"/> is the same as or newer than <paramref name="floor"/>,
    /// compared component by component rather than as text.
    /// </summary>
    /// <remarks>
    /// String comparison would get "3.9.0" vs "3.15.0" backwards, which is exactly the pair this has
    /// to judge — Material.Icons went 3.0.0 to 3.0.2 and Material.Avalonia 3.14.2 to 3.19.0.
    /// </remarks>
    private static bool IsAtLeast(string candidate, string floor)
        => System.Version.Parse(candidate) >= System.Version.Parse(floor);

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
