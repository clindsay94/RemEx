using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Remex.Desktop.Tests.Views;

/// <summary>
/// RemEx-wdqx. Refuses a code-behind that declares its own parameterless
/// <c>InitializeComponent()</c> while its markup declares <c>x:Name</c>d controls.
/// </summary>
/// <remarks>
/// Avalonia's name generator emits <c>public void InitializeComponent(bool loadXaml = true)</c>,
/// which loads the XAML AND assigns the generated <c>x:Name</c> fields. A hand-written
/// <c>private void InitializeComponent()</c> does not collide with that — it WINS overload
/// resolution against it, because a candidate applicable in its normal form beats one that needs an
/// optional parameter defaulted. The XAML still loads, so the window renders and the designer looks
/// right, but every named field stays null and the first access throws NullReferenceException.
///
/// This is a source-level pin rather than a runtime test on purpose. RemEx-r8c6 records that there
/// is no Avalonia headless harness here, so constructing these windows in a test is not currently
/// possible — but the defect is entirely visible in the source, and a scan catches the NEXT instance
/// rather than only the three that were found. It shipped in three views at once (ConfirmationDialog,
/// RestorePromptWindow, TrayBalloonWindow) because the pattern was copy-pasted between them, which is
/// exactly the failure mode a per-instance fix would leave open.
/// </remarks>
public class NameScopeWiringGuardTests
{
    // [CallerFilePath] rather than walking up from the assembly, so building with --artifacts-path
    // outside the repo does not break this with an unrelated-looking error (RemEx-6i1l).
    private static string RepoRoot([CallerFilePath] string thisSourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisSourceFile)!, "..", ".."));

    /// <summary>Matches a declaration of InitializeComponent that takes no parameters at all.</summary>
    private static readonly Regex ParameterlessInitializeComponent =
        new(@"\bvoid\s+InitializeComponent\s*\(\s*\)", RegexOptions.Compiled);

    /// <summary>
    /// Matches a named control in markup. Both <c>x:Name</c> and a bare <c>Name</c> generate a
    /// strongly-typed field — <c>ShellView.axaml</c> uses <c>Name="PageHost"</c> and the generator
    /// emits <c>PageHost</c> for it — so matching only the prefixed form would leave the hole this
    /// guard exists to close. The lookbehind keeps it from matching a longer attribute that merely
    /// ends in "Name", such as <c>SensorName=</c> or <c>x:DataType.Name=</c>.
    ///
    /// This is a deliberately conservative proxy, and it over-reports in one direction: a name
    /// declared inside a <c>ControlTemplate</c> or <c>DataTemplate</c> belongs to that template's
    /// namescope and generates NO field, but this regex still counts it. Over-reporting is the
    /// fail-safe direction — it can only ask for a harmless deletion — so it is not worth parsing
    /// template scopes to avoid. Meet it as a known imprecision rather than evidence the guard is wrong.
    /// </summary>
    private static readonly Regex ControlName =
        new(@"(?<![\w:.])(?:x:)?Name\s*=\s*""([A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.Compiled);

    [Fact]
    public void NoCodeBehindShadowsTheGeneratedInitializeComponentWhileUsingNamedControls()
    {
        var root = RepoRoot();
        Assert.True(Directory.Exists(root), $"expected the repo root at {root}");

        var offenders = new List<string>();
        var scanned = 0;

        // Scanned from the repo root rather than from remex.desktop. Every .axaml.cs lives there
        // today, so the two are equivalent — but a second Avalonia-hosting project added later would
        // otherwise never be scanned, silently, with this guard still green.
        foreach (var codeBehind in Directory.EnumerateFiles(root, "*.axaml.cs", SearchOption.AllDirectories))
        {
            // Skip build output — generated copies are not the source of truth. Also skip artifacts/,
            // which is where UseArtifactsOutput puts binaries for this repo.
            //
            // And skip .claude/, which is gitignored and is where agent worktrees are checked out.
            // A worktree there is a WHOLE OTHER BRANCH sitting inside this working copy, so scanning
            // it makes this guard report defects that are not in the tree being verified, cannot be
            // fixed from it, and may belong to a session still working on them. It happened: a
            // worktree holding a pre-RemEx-wdqx checkout turned the gate red for four files that had
            // been fixed here months earlier (RemEx-cwfrq).
            var relative = Path.GetRelativePath(root, codeBehind).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.StartsWith("artifacts/", StringComparison.Ordinal)
                || relative.StartsWith(".claude/", StringComparison.Ordinal))
            {
                continue;
            }

            var markup = codeBehind[..^3]; // strip the trailing ".cs" to get the .axaml
            if (!File.Exists(markup))
            {
                continue;
            }

            scanned++;

            // Strip comments before scanning: this guard's own explanation, and the notes left in the
            // three fixed files, both name the banned declaration in prose. A scan that read those as
            // violations would fail forever on the fix that closed the bug.
            var source = File.ReadAllText(codeBehind);
            source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            source = Regex.Replace(source, @"//[^\n]*", string.Empty);

            if (!ParameterlessInitializeComponent.IsMatch(source))
            {
                continue;
            }

            // Declaring it is only a defect when the markup actually has named controls to wire up.
            // A view that names nothing has nothing to lose, and several in this repo are written
            // that way — the guard allows those rather than demanding a cosmetic deletion.
            var names = ControlName.Matches(File.ReadAllText(markup)).Select(m => m.Groups[1].Value).Distinct().ToList();
            if (names.Count > 0)
            {
                offenders.Add($"{relative} shadows the generated InitializeComponent while its markup names: {string.Join(", ", names)}");
            }
        }

        Assert.True(scanned > 0, "scanned no code-behind files at all — the search path is wrong, not the code.");
        Assert.True(
            offenders.Count == 0,
            "A code-behind declares a parameterless InitializeComponent() while its markup declares x:Name'd controls.\n"
            + "That shadows Avalonia's generated InitializeComponent(bool loadXaml = true), so the XAML loads but the\n"
            + "generated name fields are never assigned and the first access throws NullReferenceException.\n"
            + "Delete the hand-written method and let the generated one run (RemEx-wdqx):\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void TheGuardWouldHaveCaughtTheOriginalDefect()
    {
        // Defect injection, in-memory: the exact shape the three broken views had. If the regex above
        // is ever loosened into uselessness, this fails and says so, instead of the real scan quietly
        // passing over a reintroduced bug.
        const string brokenCodeBehind = """
            public partial class Example : Window
            {
                public Example() { InitializeComponent(); TitleText.Text = "x"; }
                private void InitializeComponent() { AvaloniaXamlLoader.Load(this); }
            }
            """;
        const string markupWithNames = """<TextBlock x:Name="TitleText" />""";

        Assert.Matches(ParameterlessInitializeComponent, brokenCodeBehind);
        Assert.Single(ControlName.Matches(markupWithNames));

        // A BARE Name= generates a field just the same — ShellView.axaml does exactly this with
        // Name="PageHost". Matching only the x: form would leave the guard blind in the direction it
        // was built to cover, so pin both. Found in review of this very change.
        Assert.Single(ControlName.Matches("""<ContentControl Name="PageHost" />"""));

        // ...but an attribute that merely ENDS in Name is not a named control, or the guard would
        // report offenders for markup that has none.
        Assert.Empty(ControlName.Matches("""<Sensor SensorName="cpu" DisplayName="CPU" />"""));

        // And the fixed shape must NOT match, or the guard would block the correct code.
        const string fixedCodeBehind = """
            public partial class Example : Window
            {
                public Example() { InitializeComponent(); TitleText.Text = "x"; }
            }
            """;
        Assert.DoesNotMatch(ParameterlessInitializeComponent, fixedCodeBehind);

        // A generated-style declaration with the optional parameter is not the banned form either.
        const string generatedForm = "public void InitializeComponent(bool loadXaml = true) { }";
        Assert.DoesNotMatch(ParameterlessInitializeComponent, generatedForm);
    }
}
