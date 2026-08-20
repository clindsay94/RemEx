using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Remex.Desktop.Services;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Every desktop build carries an identity for THAT BUILD, distinct from the release version
/// (RemEx-2ckhm).
/// </summary>
/// <remarks>
/// <para>
/// WHY IT EXISTS. Both heads shipped 2.4.0 for months, so the version number could not tell two
/// binaries apart — and "is the fix in the build I am looking at?" is the question a review session
/// asks constantly. It was answered twice on this branch by comparing a file timestamp against a
/// commit timestamp, and one of those answers was wrong: a pin fix was reviewed as broken when it
/// had simply not been deployed, and the bead had to be reopened.
/// </para>
/// <para>
/// WHAT IS DELIBERATELY NOT ASSERTED: that the stamp equals the CURRENT <c>git rev-parse HEAD</c>.
/// It legitimately will not during normal work — build, then commit, and the assembly on disk
/// honestly records the commit it was built from. Asserting equality would fail on a correct tree
/// every time anyone committed after building, which is most of the time, and a test that cries
/// wolf gets deleted rather than heeded.
/// </para>
/// </remarks>
public sealed class BuildIdTests
{
    /// <summary>Seven hex for the short sha, optionally "+" and four hex for a dirty tree.</summary>
    private const string Shape = @"^[0-9a-f]{7}(\+[0-9a-f]{4})?$";

    [Fact]
    public void TheDesktopAssemblyCarriesAWellFormedBuildId()
    {
        var id = AppVersion.ResolveBuildId(typeof(AppVersion).Assembly);

        id.Should().NotBeEmpty(
            "remex.desktop opts into stamping via RemexStampBuildId; an empty id here means the "
            + "target stopped running, and the About page would silently hide the row rather than fail");
        id.Should().MatchRegex(Shape,
            "the id is read off a screen and typed into a message — an unexpected shape means the "
            + "targets file is emitting something nobody can transcribe");
    }

    [Fact]
    public void TheBuildIdIsNotTheVersion()
    {
        // They answer different questions and are shown as separate rows. If a refactor ever wires
        // one to the other, the About page grows a row that adds nothing.
        //
        // ASSERTED ON THE PROPERTY, NOT ONLY ON THE METHOD, and that distinction is not pedantic:
        // the first version of this class only ever called ResolveBuildId, so repointing the BuildId
        // PROPERTY at Resolve — making the build-id row display the version — passed every test
        // here. AboutViewModel reads the property, so the property is what the user sees.
        AppVersion.BuildId.Should().NotBe(AppVersion.Display);
        AppVersion.BuildId.Should().Be(AppVersion.ResolveBuildId(typeof(AppVersion).Assembly),
            "the property must be the stamp, not a second opinion about the version");
    }

    [Fact]
    public void AnUnstampedAssemblyReportsNoBuildIdRatherThanThrowing()
    {
        // Every other assembly in the process is unstamped, including this test one. Reading a
        // missing stamp has to be ordinary, because the About page constructs its view model before
        // it could possibly handle an exception from it.
        AppVersion.ResolveBuildId(typeof(BuildIdTests).Assembly).Should().BeEmpty();
        AppVersion.ResolveBuildId(typeof(string).Assembly).Should().BeEmpty();
    }

    [Fact]
    public void TheLiteralUnknownIsTreatedAsAbsent()
    {
        // The targets file writes "unknown" when git is unavailable — a source drop with no .git, a
        // build machine without git on PATH. That is a fact about the BUILD MACHINE, not about the
        // build, so the About row hides instead of displaying a word that invites the reader to
        // conclude something about the binary.
        var assembly = new StubAssembly("unknown");
        AppVersion.ResolveBuildId(assembly).Should().BeEmpty();

        // Anti-vacuity: the same stub with a real value must come back intact, or the assertion
        // above would pass for a method that returns empty unconditionally.
        AppVersion.ResolveBuildId(new StubAssembly("abc1234")).Should().Be("abc1234");
    }

    [Fact]
    public void TheStampingTargetIsWiredToTheProjectThatShowsIt()
    {
        // A missing opt-in is invisible: the target simply does not run, the id is empty, and the
        // About row hides itself. Nothing fails, and the feature is gone. Pin both halves of the
        // wiring in source, since neither is observable from a built assembly that lacks the stamp.
        var props = File.ReadAllText(Path.Combine(RepoRoot(), "Directory.Build.props"));
        props.Should().Contain("build\\BuildId.targets",
            "the targets file has to be imported or nothing stamps anything");

        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "remex.desktop", "remex.desktop.csproj"));
        csproj.Should().Contain("<RemexStampBuildId>true</RemexStampBuildId>",
            "remex.desktop owns the About page and is the assembly that must carry the stamp");

        // COMMENTS STRIPPED FIRST, and the first version of this did not do that. BuildId.targets
        // explains in prose why it uses StableStringHash rather than String.GetHashCode — so
        // swapping the actual call for GetHashCode left the WORD "StableStringHash" sitting in the
        // comment two lines above, and the assertion below passed on the injected defect. A comment
        // that mentions the thing it is warning about will satisfy any test that greps the raw file.
        var targets = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "build", "BuildId.targets")),
            @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        targets.Should().Contain("WriteOnlyWhenDifferent=\"true\"",
            "without this every build rewrites the generated file and forces a full recompile");
        targets.Should().Contain("StableStringHash",
            "String.GetHashCode is randomised per process, so the dirty suffix would change on every "
            + "build and rebuild the assembly each time");
    }

    /// <summary>An <see cref="Assembly"/> carrying exactly one build-id stamp and nothing else.</summary>
    /// <remarks>
    /// Hand-rolled rather than emitted at run time: a dynamic assembly with a custom attribute needs
    /// Reflection.Emit, and this only has to answer one question.
    /// </remarks>
    private sealed class StubAssembly : Assembly
    {
        // THE EXACT ELEMENT TYPE MATTERS. CustomAttributeExtensions.GetCustomAttributes<T> CASTS the
        // array this returns to IEnumerable<T> rather than copying it, so anything looser — object[]
        // or even Attribute[] — throws InvalidCastException from inside the BCL, pointing at
        // AppVersion rather than at the stub that caused it. Both wrong types were tried before this
        // one, and neither error named this file.
        private readonly AssemblyMetadataAttribute[] _attributes;

        public StubAssembly(string buildId) =>
            _attributes = new[] { new AssemblyMetadataAttribute("RemexBuildId", buildId) };

        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => _attributes;

        public override object[] GetCustomAttributes(bool inherit) => _attributes;
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
