using Remex.Agent.Services.Readiness;
using Xunit;

namespace Remex.Agent.Tests;

/// <summary>
/// Tests for the firewall readiness interpretation (RemEx-ksbm).
/// </summary>
/// <remarks>
/// This row is the one that stops the card lying by omission. A green port-listening row means the
/// server is up, not that anything can reach it — the firewall sits between the two — so without
/// this row the card can show all-green on a machine the phone provably cannot reach.
///
/// Two directions of error matter here and they are not symmetric. Reporting "blocked" on a working
/// machine sends a non-technical user to change firewall settings that were correct. Reporting
/// "allowed" on a blocked one is worse: it removes the first thing they would have looked at.
/// </remarks>
public class FirewallReadinessTests
{
    private const string ExePath = @"C:\Program Files\RemEx\Remex.Agent.exe";

    private static WindowsFirewallRule Rule(
        bool enabled = true, bool? allows = true, string? program = ExePath, string name = "RemexHostInbound") =>
        new(name, enabled, allows, program);

    [Fact]
    public void AnEnabledAllowRuleForThisExeIsAllowed()
    {
        Assert.True(FirewallReadiness.InterpretWindows([Rule()], ExePath));
    }

    [Fact]
    public void NoRuleAtAllIsRefused_NotUnknown()
    {
        // Windows denies unsolicited inbound by default, so the ABSENCE of our rule is not missing
        // information - it is the reason the phone cannot connect. This is the state a machine lands
        // in when the installer's firewall step failed, which autostart-remex.ps1 only warns about
        // and carries on from, so it is a state real users reach.
        Assert.False(FirewallReadiness.InterpretWindows([], ExePath));
    }

    [Fact]
    public void ADisabledRuleIsRefusedEvenThoughItExists()
    {
        // "The rule exists" is the wrong question. A user can switch a rule off in the Windows UI
        // without deleting it, and a disabled rule permits exactly nothing.
        Assert.False(FirewallReadiness.InterpretWindows([Rule(enabled: false)], ExePath));
    }

    [Fact]
    public void AnEnabledBlockRuleDefeatsAnAllowRuleBesideIt()
    {
        // WINDOWS EVALUATES BLOCK BEFORE ALLOW. An "any rule allows" test would report green on a
        // machine that is provably refusing us, which is the worse of the two error directions.
        var rules = new[]
        {
            Rule(allows: true, name: "RemexHostInbound"),
            Rule(allows: false, name: "RemexClientInbound")
        };

        Assert.False(FirewallReadiness.InterpretWindows(rules, ExePath));
    }

    [Fact]
    public void ADisabledBlockRuleDoesNotDefeatAnEnabledAllowRule()
    {
        // The mirror of the case above, and the one that stops the block check from being a blunt
        // "any block anywhere". A switched-off block rule blocks nothing.
        var rules = new[]
        {
            Rule(allows: true, name: "RemexHostInbound"),
            Rule(enabled: false, allows: false, name: "RemexClientInbound")
        };

        Assert.True(FirewallReadiness.InterpretWindows(rules, ExePath));
    }

    [Fact]
    public void ARuleScopedToADifferentExecutableDoesNotCoverUs()
    {
        // THE DRIFT CASE. The installer scopes its rules with -Program, so a rule that survives an
        // update which moved the install directory still exists, is still enabled, and still allows
        // - a different file. Everything looks configured and nothing is permitted.
        var stale = Rule(program: @"C:\Program Files (x86)\RemEx\Remex.Agent.exe");

        Assert.False(FirewallReadiness.InterpretWindows([stale], ExePath));
    }

    [Fact]
    public void ProgramPathsAreComparedCaseInsensitively()
    {
        // NTFS is case-insensitive, so an ordinal comparison would report "the rule does not cover
        // you" for a rule that provably does. This does NOT contradict the repo's case-sensitivity
        // rule, which is about paths that must survive a case-sensitive Linux filesystem - this
        // comparison is Windows-only by construction.
        var shouty = Rule(program: ExePath.ToUpperInvariant());

        Assert.True(FirewallReadiness.InterpretWindows([shouty], ExePath));
    }

    [Fact]
    public void AnEnvironmentVariableInTheRulePathIsExpandedBeforeComparing()
    {
        // The firewall stores whatever it was given, and an installer or policy tool may have given
        // it an unexpanded form. Comparing the literal text would fail on a machine that is
        // correctly configured.
        var withVariable = Rule(program: @"%ProgramFiles%\RemEx\Remex.Agent.exe");
        var expanded = Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\RemEx\Remex.Agent.exe");

        Assert.True(FirewallReadiness.InterpretWindows([withVariable], expanded));
    }

    [Fact]
    public void ARuleWithNoProgramScopeAllowsWhateverServesThePort()
    {
        // Nothing to compare, so nothing to get wrong - the rule permits the traffic regardless of
        // which executable answers.
        Assert.True(FirewallReadiness.InterpretWindows([Rule(program: null)], ExePath));
    }

    [Fact]
    public void AProgramScopedRuleIsUnknownWhenOurOwnPathIsUnavailable()
    {
        // Environment.ProcessPath can be null in hosting arrangements this app does not control.
        // Answering "refused" would blame the firewall for our own missing information.
        Assert.Null(FirewallReadiness.InterpretWindows([Rule()], null));
        Assert.Null(FirewallReadiness.InterpretWindows([Rule()], "   "));
    }

    [Fact]
    public void OneMatchingRuleAmongSeveralIsEnough()
    {
        var rules = new[]
        {
            Rule(program: @"C:\Old\Remex.Agent.exe", name: "RemexClientInbound"),
            Rule(program: ExePath, name: "RemexHostInbound")
        };

        Assert.True(FirewallReadiness.InterpretWindows(rules, ExePath));
    }

    [Fact]
    public void TheRuleNamesQueriedAreTheInvariantOnesTheInstallerCreates()
    {
        // THE LOCALIZATION TRAP THIS FEATURE HAD TO AVOID. autostart-remex.ps1 creates its rules
        // with -Name RemexHostInbound / RemexClientInbound and -DisplayName "RemEx". The Name is
        // ours and is never translated; the DisplayName is what the Windows UI and localized netsh
        // output show. Matching display text would silently stop working in eight of the nine
        // languages this app ships in.
        Assert.Equal(["RemexHostInbound", "RemexClientInbound"], FirewallReadiness.WindowsRuleNames);
    }

    [Fact]
    public void FirewalldOpenPortIsAllowedAndClosedPortIsRefused()
    {
        // EXIT CODES, NOT OUTPUT. firewalld's messages go through gettext and are translated on a
        // localized system; its exit codes are not. Same trap as the Windows DisplayName, avoided
        // the same way.
        Assert.True(FirewallReadiness.InterpretFirewalld(stateExitCode: 0, queryPortExitCode: 0));
        Assert.False(FirewallReadiness.InterpretFirewalld(stateExitCode: 0, queryPortExitCode: 1));
    }

    [Fact]
    public void FirewalldNotRunningIsUnknownRatherThanAllowed()
    {
        // It rules FIREWALLD out; it does not rule out nftables or iptables rules loaded directly.
        // Returning true here would be a guess, and it would be wrong on precisely the
        // hand-configured machines whose owners are least likely to suspect their own firewall.
        Assert.Null(FirewallReadiness.InterpretFirewalld(stateExitCode: 252, queryPortExitCode: 0));
    }

    [Fact]
    public void AnUnrecognisedFirewalldExitCodeIsUnknownRatherThanRefused()
    {
        // firewall-cmd documents 1 as "the query is false". Anything else is the CALL failing - a
        // D-Bus refusal, a bad argument, a missing zone - and answering "not open" to those reports
        // a firewall problem the user does not have.
        Assert.Null(FirewallReadiness.InterpretFirewalld(stateExitCode: 0, queryPortExitCode: 2));
        Assert.Null(FirewallReadiness.InterpretFirewalld(stateExitCode: 0, queryPortExitCode: int.MinValue));
    }

    [Fact]
    public void UfwDisabledIsAllowedBecauseItIsNotFilteringAtAll()
    {
        // The one thing an unprivileged process CAN establish about ufw. ufw.conf is world-readable
        // and carries ENABLED=; the rules live in user.rules, which is root-only.
        Assert.True(FirewallReadiness.InterpretUfw("ENABLED=no\nLOGLEVEL=low\n"));
    }

    [Fact]
    public void UfwEnabledIsUnknownRatherThanAllowedOrRefused()
    {
        // WE CANNOT READ THE RULES, so we do not know whether this port is among them. Guessing
        // "allowed" would show green on exactly the machines most likely to be blocking, since a
        // user who turned ufw on is the user whose phone cannot connect. Guessing "refused" would
        // send someone to change a firewall that was already correct.
        Assert.Null(FirewallReadiness.InterpretUfw("ENABLED=yes\nLOGLEVEL=low\n"));
    }

    [Fact]
    public void UfwAbsentIsUnknown()
    {
        // Null contents is how "not installed" and "unreadable" both arrive.
        Assert.Null(FirewallReadiness.InterpretUfw(null));
        Assert.Null(FirewallReadiness.InterpretUfw("LOGLEVEL=low\n"));
    }

    [Fact]
    public void ACommentedOutEnabledLineIsNotReadAsConfiguration()
    {
        // The shipped ufw.conf documents this key in prose directly above it, so the obvious
        // implementation - Contains("ENABLED=no") over the whole file - reads the documentation as
        // the setting, and reads it as DISABLED, which is the GREEN answer. Reporting "no firewall
        // is filtering" on a machine whose firewall is on is the worse of the two error directions:
        // it removes the first thing the user would have looked at.
        //
        // Mutation-checked: this is the assertion that kills a Contains-based parse. An explicit
        // comment-skipping branch in the implementation does NOT kill anything, because anchoring
        // the match to the start of a trimmed line already excludes comments - that branch was
        // written, found to be unkillable, and removed as dead code.
        var conf = "# Set to yes to start ufw on boot.\n# ENABLED=no\nENABLED=yes\n";

        Assert.Null(FirewallReadiness.InterpretUfw(conf));
    }

    [Fact]
    public void AnIndentedEnabledLineIsStillRead()
    {
        // The anchoring is on the TRIMMED line, so leading whitespace is tolerated. Without the
        // trim, a hand-edited file with an indented setting would fall through to Unknown and the
        // row would go amber on a machine that is perfectly configured.
        Assert.True(FirewallReadiness.InterpretUfw("   ENABLED=no\n"));
    }

    [Fact]
    public void AnUnrecognisedUfwValueIsUnknownRatherThanAssumedDisabled()
    {
        Assert.Null(FirewallReadiness.InterpretUfw("ENABLED=maybe\n"));
        Assert.Null(FirewallReadiness.InterpretUfw("ENABLED=\n"));
    }

    [Fact]
    public void AStaleBlockRuleScopedToAnOldPathDoesNotRefuseUs()
    {
        // REVIEW CAUGHT THIS ASYMMETRY. The block check originally ignored program scope while the
        // allow check required it, so a block rule left pointing at a previous install directory -
        // the very drift the allow path is written to survive - reported "the firewall is refusing
        // inbound traffic" while Windows was applying that rule to a file that does not run.
        var rules = new[]
        {
            Rule(allows: true, program: ExePath, name: "RemexHostInbound"),
            Rule(allows: false, program: @"C:\Program Files (x86)\RemEx\Remex.Agent.exe", name: "RemexClientInbound")
        };

        Assert.True(FirewallReadiness.InterpretWindows(rules, ExePath));
    }

    [Fact]
    public void ABlockRuleWeCannotPlaceIsUnknownRatherThanIgnored()
    {
        // The conservative half of the same rule. If we cannot tell which executable a block rule
        // names, we cannot tell whether we are refused - and optimistically ignoring it would report
        // green on a machine that is blocking.
        var rules = new[] { Rule(allows: true, program: null), Rule(allows: false, program: ExePath) };

        Assert.Null(FirewallReadiness.InterpretWindows(rules, agentExecutablePath: null));
    }

    [Fact]
    public void AnActionThatIsNeitherAllowNorBlockIsUnknownRatherThanRefused()
    {
        // NotConfigured is a real member of the firewall's Action enum and turns up on rules sourced
        // from Group Policy. Folding it into Block - the obvious two-valued mapping - would report a
        // refusal that is not happening, on domain machines specifically.
        Assert.Null(FirewallReadiness.InterpretWindows([Rule(allows: null)], ExePath));
    }

    [Fact]
    public void AnUnknownActionScopedToADifferentExecutableIsIrrelevant()
    {
        // It cannot refuse traffic it does not cover, so it must not hold the answer open.
        var rules = new[]
        {
            Rule(allows: null, program: @"C:\Other\thing.exe", name: "RemexClientInbound"),
            Rule(allows: true, program: ExePath, name: "RemexHostInbound")
        };

        Assert.True(FirewallReadiness.InterpretWindows(rules, ExePath));
    }

    [Fact]
    public void TheFirewallsWordForUnscopedIsTreatedAsNoScope()
    {
        // "Any" IS A STRING, NOT AN ABSENCE. Get-NetFirewallApplicationFilter returns the literal
        // "Any" for a rule that is not program-scoped, so the null check alone could never fire from
        // a real query - the path comparison instead compared "Any" against the agent's executable,
        // failed, and reported the firewall as refusing traffic for a rule that permits everything.
        Assert.Null(FirewallQuery.NormalizeProgramScope("Any"));
        Assert.Null(FirewallQuery.NormalizeProgramScope("any"));
        Assert.Null(FirewallQuery.NormalizeProgramScope("  "));
        Assert.Equal(ExePath, FirewallQuery.NormalizeProgramScope(ExePath));
    }

    [Fact]
    public void ARuleReportedAsUnscopedByTheRealQueryShapeIsAllowed()
    {
        // End to end through the parser, in the shape the live cmdlet actually produces, rather than
        // through a hand-built record that could not occur.
        var rules = FirewallQuery.ParseWindowsRules("RULE|RemexHostInbound|1|1|Any\n");

        Assert.True(FirewallReadiness.InterpretWindows(rules, ExePath));
    }

    [Fact]
    public void TheWindowsRuleParserIgnoresAnythingItDoesNotRecognise()
    {
        // The script's output is a format this app defines, but PowerShell can still interleave a
        // warning or a blank line. Anything that is not a RULE record is not a rule.
        // The five-field NOTRULE line is the one that matters: without the tag check, a field-count
        // test alone would accept it. Review caught that the original fixture's junk lines were all
        // single-field, so deleting the tag check killed nothing.
        var output = "WARNING: something\nNOTRULE|x|1|1|C:\\evil.exe\n" +
                     "RULE|RemexHostInbound|1|1|C:\\a\\b.exe\n\ngarbage\n";

        var rules = FirewallQuery.ParseWindowsRules(output);

        var rule = Assert.Single(rules);
        Assert.Equal("RemexHostInbound", rule.Name);
        Assert.True(rule.Enabled);
        Assert.True(rule.Allows);
        Assert.Equal("C:\\a\\b.exe", rule.ProgramPath);
    }

    [Fact]
    public void AScriptThatDidNotReachItsEndIsNotAVerdict()
    {
        // THE DISTINCTION AN EXIT CODE CANNOT MAKE. powershell.exe's exit code reflects the success
        // of THE LAST STATEMENT EXECUTED, not whether any error record was produced - a handled
        // error partway through exits 0, a failure in the final statement exits 1. So the exit-code
        // gate this replaces failed exactly when the LAST rule queried was missing and passed when
        // the first was, i.e. it depended on the ORDER of the rule names. That turned the state the
        // row exists for ("the installer's firewall step failed") into "could not check" some of the
        // time, showing amber instead of red on precisely the machine that is broken.
        Assert.False(FirewallQuery.ScriptRanToCompletion(""));
        Assert.False(FirewallQuery.ScriptRanToCompletion("RULE|A|1|1|C:\\a.exe\n"));

        // Found nothing AND reached the end: a real, reportable "no rule exists".
        Assert.True(FirewallQuery.ScriptRanToCompletion(FirewallQuery.CompletionSentinel + "\n"));
        Assert.True(FirewallQuery.ScriptRanToCompletion(
            "RULE|A|1|1|C:\\a.exe\r\n" + FirewallQuery.CompletionSentinel + "\r\n"));
    }

    [Fact]
    public void TheSentinelIsMatchedWholeRatherThanAsASubstring()
    {
        // A rule whose name merely contained the sentinel must not be mistaken for the end marker,
        // or a hostile or unlucky rule name would fabricate a completed run.
        Assert.False(FirewallQuery.ScriptRanToCompletion(
            $"RULE|{FirewallQuery.CompletionSentinel}-not-really|1|1|C:\\a.exe\n"));
    }

    [Fact]
    public void TheWindowsRuleParserAcceptsTheCarriageReturnsProductionActuallyEmits()
    {
        // The parser is fed AppendLine output, which is CRLF on Windows, while every other fixture
        // here uses a bare \n. WHAT ABSORBS THE CARRIAGE RETURN IS NOT THE PARSER, and review
        // corrected that: splitting on '\n' leaves the '\r' at the END of the line, so it lands
        // inside the program field and NormalizeProgramScope's own trim removes it. A line-level
        // Trim in the parser looked like the mechanism, could not be killed by any mutant, and was
        // deleted. This test kills the mutant that IS load-bearing - dropping the trim inside
        // NormalizeProgramScope, which would leave every program path ending in '\r' and fail every
        // comparison against the running executable.
        var rules = FirewallQuery.ParseWindowsRules("RULE|R|1|1|C:\\a.exe\r\nREMEX-FW-DONE\r\n");

        Assert.Equal("C:\\a.exe", Assert.Single(rules).ProgramPath);
    }

    [Fact]
    public void TheWindowsRuleParserReadsAllThreeFieldStates()
    {
        // Enabled=0 and the '?' action marker were never exercised, so a parser that hardcoded
        // either would have passed. Both states are ones a real machine produces.
        var rules = FirewallQuery.ParseWindowsRules(
            "RULE|A|0|1|C:\\a.exe\nRULE|B|1|0|C:\\b.exe\nRULE|C|1|?|C:\\c.exe\n");

        Assert.Equal(3, rules.Count);
        Assert.False(rules[0].Enabled);
        Assert.True(rules[0].Allows);
        Assert.False(rules[1].Allows);
        Assert.Null(rules[2].Allows);
    }

    [Fact]
    public void TheWindowsRuleParserKeepsAPathContainingTheDelimiterIntact()
    {
        // Split with a field limit rather than unbounded, so a path containing the delimiter cannot
        // shift the fields and turn an allow rule into a block one. A Windows path cannot contain
        // '|', but the parser should not be the thing that depends on that.
        var rules = FirewallQuery.ParseWindowsRules("RULE|R|1|0|C:\\weird|name.exe\n");

        var rule = Assert.Single(rules);
        Assert.False(rule.Allows);
        Assert.Equal("C:\\weird|name.exe", rule.ProgramPath);
    }

    [Fact]
    public void TheWindowsRuleParserTreatsAnEmptyProgramFieldAsNoScope()
    {
        var rules = FirewallQuery.ParseWindowsRules("RULE|R|1|1|\n");

        Assert.Null(Assert.Single(rules).ProgramPath);
    }
}
