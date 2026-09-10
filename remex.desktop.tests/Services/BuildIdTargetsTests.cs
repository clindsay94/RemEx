using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Remex.Desktop.Tests.Services;

/// <summary>
/// Exercises <c>build/BuildId.targets</c> against a purpose-built git repository, so the clean and
/// dirty branches are measured rather than read (RemEx-2ckhm).
/// </summary>
/// <remarks>
/// <para>
/// THE DIRTY BRANCH IS THE ONLY REASON THIS FEATURE EXISTS. The .NET SDK already embeds the commit
/// sha in AssemblyInformationalVersion for free; what it cannot do is tell you the binary was built
/// from uncommitted work, which is exactly the case where the sha is a lie shared with a different
/// binary. Every other test in this area asserts the SHAPE of the id on the real build, and a
/// targets file that quietly stopped detecting dirtiness would satisfy all of them — a clean-looking
/// id is a valid id.
/// </para>
/// <para>
/// SO THIS ONE RUNS MSBUILD FOR REAL, against a temp repo it creates, commits into, and then
/// dirties. It costs a few seconds and needs git on PATH; it skips rather than fails when git is
/// missing, because a machine without git is a fact about the machine, and this suite also runs on
/// build agents that may be a source drop.
/// </para>
/// </remarks>
public sealed class BuildIdTargetsTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "remex-buildid-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void ACleanTreeStampsTheShortShaAlone()
    {
        if (!GitAvailable()) return;

        InitRepoWithOneCommit();
        var id = Evaluate();

        id.Should().MatchRegex(@"^[0-9a-f]{7}$",
            "a clean tree has nothing to disambiguate, so the sha stands alone");
        id.Should().Be(Git("rev-parse --short=7 HEAD").Trim(),
            "and it must be THIS repo's HEAD, not a value carried over from the one being built");
    }

    [Fact]
    public void AModifiedTrackedFileAddsTheDirtyMarker()
    {
        if (!GitAvailable()) return;

        InitRepoWithOneCommit();
        var clean = Evaluate();

        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "edited");
        var dirty = Evaluate();

        dirty.Should().MatchRegex(@"^[0-9a-f]{7}\+[0-9a-f]{4}$");
        dirty.Should().StartWith(clean,
            "the sha half still names the commit the build started from");
        dirty.Should().NotBe(clean,
            "which is the whole point — two different binaries must not share one identity");
    }

    [Fact]
    public void AnUntrackedFileCountsAsDirtyToo()
    {
        // Untracked files are the half of "dirty" that is easy to miss, and the more dangerous half:
        // a new source file that is compiled in but never committed is invisible to `git diff`.
        if (!GitAvailable()) return;

        InitRepoWithOneCommit();
        var clean = Evaluate();

        File.WriteAllText(Path.Combine(_repo, "brand-new.txt"), "never committed");

        Evaluate().Should().NotBe(clean).And.Contain("+");
    }

    [Fact]
    public void DifferentDirtyFilesProduceDifferentMarkers()
    {
        // The suffix distinguishes which FILES changed. It does not distinguish two different edits
        // to the same file — build/BuildId.targets says so plainly — but this much it must do, or it
        // is a constant wearing a hash's clothes.
        if (!GitAvailable()) return;

        InitRepoWithOneCommit();

        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "edited");
        var first = Evaluate();

        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "restored\n");
        File.WriteAllText(Path.Combine(_repo, "other.txt"), "a different file");
        var second = Evaluate();

        second.Should().NotBe(first);
    }

    [Fact]
    public void ADirectoryThatIsNotARepositoryStampsUnknownRatherThanFailingTheBuild()
    {
        // A source drop with no .git must still build. An About page that hides its build-id row is
        // a diagnosable state; a build that refuses to run over a missing .git directory is not.
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "no repo here");

        Evaluate().Should().Be("unknown");
    }

    /// <summary>
    /// Runs the stamping target against <see cref="_repo"/> and returns the id it wrote.
    /// </summary>
    private string Evaluate()
    {
        // OUTSIDE the repo, deliberately. The first version of this put the probe project and its
        // generated file inside _repo, which made every "clean" repo dirty and failed the clean-tree
        // test — the test working correctly on a broken harness. In the real build the equivalent
        // output lives under artifacts/, which .gitignore covers, so a build does not dirty its own
        // tree; a probe project cannot rely on that.
        var projectDir = _repo + "-probe";
        Directory.CreateDirectory(projectDir);

        // A bare project that imports ONLY the targets file — no SDK, no Directory.Build.props — so
        // what is measured is this file's logic and not the surrounding build.
        var project = Path.Combine(projectDir, "probe.proj");
        File.WriteAllText(project,
            "<Project>\n"
            + "  <PropertyGroup>\n"
            + "    <RemexStampBuildId>true</RemexStampBuildId>\n"
            + "    <IntermediateOutputPath>" + projectDir + Path.DirectorySeparatorChar + "</IntermediateOutputPath>\n"
            + "  </PropertyGroup>\n"
            + "  <Import Project=\"" + Path.Combine(RepoRoot(), "build", "BuildId.targets") + "\" />\n"
            + "  <Target Name=\"Build\" DependsOnTargets=\"RemexComputeBuildId\" />\n"
            + "</Project>\n");

        var exit = Run("dotnet", $"msbuild \"{project}\" -t:Build -nologo -v:quiet -p:RemexBuildIdRepoRoot=\"{_repo}\"",
            projectDir, out var output);
        exit.Should().Be(0, "the stamping target must never fail a build: " + output);

        var stamp = Path.Combine(projectDir, "RemexBuildId.g.cs");
        File.Exists(stamp).Should().BeTrue("the target did not write the generated file: " + output);

        var match = Regex.Match(File.ReadAllText(stamp), @"""RemexBuildId"",\s*""([^""]*)""\)");
        match.Success.Should().BeTrue("the generated file's shape changed; re-point this test");
        return match.Groups[1].Value;
    }

    private void InitRepoWithOneCommit()
    {
        Directory.CreateDirectory(_repo);
        Git("init -q");
        // Identity and signing set locally: the machine's global config may require a GPG key, and a
        // test that depends on the developer's git configuration is a test that fails for one person.
        Git("config user.email test@remex.invalid");
        Git("config user.name RemEx Test");
        Git("config commit.gpgsign false");

        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "original\n");
        Git("add tracked.txt");
        Git("commit -q -m seed");
    }

    private string Git(string arguments)
    {
        Run("git", "-C \"" + _repo + "\" " + arguments, _repo, out var output);
        return output;
    }

    private static bool GitAvailable() => Run("git", "--version", Path.GetTempPath(), out _) == 0;

    private static int Run(string file, string arguments, string workingDirectory, out string output)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _repo, _repo + "-probe" })
        {
            try
            {
                if (!Directory.Exists(directory)) continue;

                // git marks objects read-only, which blocks a plain recursive delete on Windows.
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }
        }
    }

    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));
}
