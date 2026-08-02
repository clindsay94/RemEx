using System.Diagnostics;
using Remex.Core.Guards;

namespace Remex.Agent.Services.Readiness;

/// <summary>The outcome of running one command: what it exited with, and what it printed.</summary>
/// <remarks>
/// <see cref="Ran"/> is separate from a zero <see cref="ExitCode"/> because "the tool is not
/// installed" and "the tool answered no" are different facts, and collapsing them is how a machine
/// with no firewall manager gets reported as a machine with a closed firewall.
/// </remarks>
public sealed record CommandResult(bool Ran, int ExitCode, string StandardOutput)
{
    /// <summary>The command could not be started at all — usually because it is not installed.</summary>
    public static readonly CommandResult NotRun = new(false, -1, string.Empty);
}

/// <summary>
/// Gathers the raw firewall facts that <see cref="FirewallReadiness"/> interprets (RemEx-ksbm).
/// </summary>
/// <remarks>
/// <para>
/// STRICTLY READ-ONLY, like every other readiness probe. Nothing here creates, enables or repairs a
/// rule. The row reports; any fix is explicit and user-invoked.
/// </para>
/// <para>
/// The command runner and file reader are injected so the platform branches are testable without a
/// firewall. That is the same split RemEx-gpe3 used for the other probes, and it exists because the
/// interpretation is where a mistake misleads someone — see <see cref="FirewallReadiness"/>.
/// </para>
/// </remarks>
public sealed class FirewallQuery
{
    private readonly Func<string, string, CommandResult> _run;
    private readonly Func<string, string?> _readFile;
    private readonly Func<string?> _agentExecutablePath;

    /// <param name="run">Runs a command with arguments and returns its result. Must not throw.</param>
    /// <param name="readFile">Reads a file, or returns null if absent or unreadable.</param>
    /// <param name="agentExecutablePath">The running executable, to match Windows rules against.</param>
    public FirewallQuery(
        Func<string, string, CommandResult> run,
        Func<string, string?> readFile,
        Func<string?> agentExecutablePath)
    {
        _run = Guard.NotNull(run);
        _readFile = Guard.NotNull(readFile);
        _agentExecutablePath = Guard.NotNull(agentExecutablePath);
    }

    /// <summary>The production instance, wired to the real process launcher and filesystem.</summary>
    public static FirewallQuery CreateDefault() =>
        new(RunCommand, ReadFileOrNull, () => Environment.ProcessPath);

    /// <summary>
    /// Whether a firewall we can see permits inbound traffic on <paramref name="port"/>.
    /// </summary>
    /// <returns>True if allowed, false if refused, null if it could not be established.</returns>
    public bool? IsInboundAllowed(int port) =>
        OperatingSystem.IsWindows() ? QueryWindows() : QueryLinux(port);

    /// <remarks>
    /// PowerShell rather than <c>netsh</c>, and the reason is localization. <c>netsh advfirewall
    /// show rule</c> prints translated field names and values on a localized Windows, so any parse
    /// of it silently stops working in eight of the nine languages this app ships in. The cmdlets
    /// return objects, and the comparison below is against .NET enum names — <c>Allow</c>,
    /// <c>True</c> — which are invariant identifiers rather than display text.
    ///
    /// The output format is one this method defines, so nothing localized reaches the parser.
    /// </remarks>
    private bool? QueryWindows()
    {
        var script = BuildWindowsQueryScript();

        var result = _run("powershell.exe", $"-NonInteractive -NoProfile -EncodedCommand {EncodeCommand(script)}");

        // THE QUERY ITSELF FAILING IS NOT A FIREWALL VERDICT. PowerShell may be blocked by policy,
        // absent from PATH, or refused by an EDR; answering "no rule found" to any of those would
        // tell the user their firewall is misconfigured when nothing established that.
        if (!result.Ran) return null;

        // GATED ON THE SENTINEL, NOT ON THE EXIT CODE, and the measured reason is narrower than the
        // obvious one. powershell.exe's exit code reflects THE SUCCESS OF THE LAST STATEMENT
        // EXECUTED — not whether any error record was produced along the way. A handled error
        // partway through exits 0; a failure in the final statement exits 1. So the previous shape
        // failed exactly when the LAST rule queried was missing, and passed when the first was:
        // an exit code that depended on the ORDER of the rule names.
        //
        // That is worse than a consistent failure, because it turned the state the row exists for
        // into "could not check" only some of the time. A machine where the installer's firewall
        // step failed (autostart-remex.ps1 only warns and carries on) went amber instead of red, and
        // the partial case discarded a rule that had been read successfully.
        //
        // The sentinel asks what the guard actually wants to know — did the script reach the end —
        // which the exit code answers only incidentally. It also happens to make the exit code 0 in
        // every case, by making the final statement one that cannot fail; that is a side effect and
        // is deliberately NOT relied upon.
        if (!OutputSupportsAVerdict(result.StandardOutput)) return null;

        var rules = ParseWindowsRules(result.StandardOutput);
        return FirewallReadiness.InterpretWindows(rules, _agentExecutablePath());
    }

    /// <summary>The query script, exposed so its failure-classification premise is testable.</summary>
    internal static string BuildWindowsQueryScript()
    {
        var names = string.Join(",", FirewallReadiness.WindowsRuleNames.Select(n => $"'{n}'"));

        // EVERY STRING LITERAL IS SINGLE-QUOTED AND THE RECORD IS BUILT BY CONCATENATION, because
        // the script is passed to PowerShell as a command-line argument and double quotes do not
        // survive that trip. Review caught the first version of this: powershell.exe argv-parses its
        // own command line and strips the inner quotes, so `Write-Output "RULE|..."` arrived
        // unquoted, failed to PARSE — before the try/catch could run — and exited 1. The branch then
        // returned null for every machine, so the row read "could not check" on every Windows PC
        // while thirteen unit tests covered code production never reached.
        //
        // -ceq, not -eq: CLAUDE.md forbids the case-insensitive form where a comparison must be
        // exact. These compare enum names with fixed casing, so the strict operator is correct.
        //
        // The Action test is three-way. NotConfigured is a real member of the enum and turns up on
        // Group Policy rules; folding it into Block would report a refusal that is not happening.
        var script =
            "$ErrorActionPreference='Stop'; foreach($n in @(" + names + ")){ " +
            "try{ $r=Get-NetFirewallRule -Name $n -ErrorAction Stop; " +
            "$p=($r|Get-NetFirewallApplicationFilter).Program; " +
            "$e=$(if(\"$($r.Enabled)\" -ceq 'True'){'1'}else{'0'}); " +
            "$a=$(if(\"$($r.Action)\" -ceq 'Allow'){'1'}elseif(\"$($r.Action)\" -ceq 'Block'){'0'}else{'?'}); " +
            "Write-Output ('RULE|'+$n+'|'+$e+'|'+$a+'|'+$p) }" +
            // ABSENCE AND FAILURE ARE DIFFERENT FACTS, and the catch used to swallow both.
            // Get-NetFirewallRule reports a missing rule as ObjectNotFound - verified on a real
            // machine rather than assumed - so anything else is the LOOKUP failing: a broken
            // NetSecurity module, a CIM failure, an EDR blocking the cmdlet. Without this the
            // two failed lookups produced zero records, which the interpreter reads as a
            // definite refusal - a query that established nothing reported to the user as
            // \"the firewall is refusing inbound traffic\". The same failed-query-as-verdict
            // class the completion sentinel fixed, one level down.
            "catch{ if(\"$($_.CategoryInfo.Category)\" -cne 'ObjectNotFound'){ " +
            "Write-Output ('RULEFAIL|'+$n) } } } " +
            "Write-Output '" + CompletionSentinel + "'";

        return script;
    }

    /// <summary>
    /// Whether the script's output can support a verdict at all.
    /// </summary>
    /// <remarks>
    /// TWO WAYS TO HAVE LEARNED NOTHING, AND BOTH MUST PRODUCE UNKNOWN. The script may not have
    /// reached its end, or it may have run perfectly and still failed to read a rule — and even ONE
    /// unreadable rule means the record set is incomplete, which cannot support the "no rule at all,
    /// therefore refused" conclusion the interpreter draws from an empty one.
    ///
    /// Combined into one internal function rather than two guards inline, because mutation testing
    /// showed the inline version unpinned: <c>QueryWindows</c> is private and Windows-gated, so
    /// deleting a guard there changed nothing any test could see.
    /// </remarks>
    internal static bool OutputSupportsAVerdict(string output) =>
        ScriptRanToCompletion(output) && !AnyRuleUnreadable(output);

    /// <summary>Whether any rule lookup failed for a reason other than the rule being absent.</summary>
    /// <remarks>
    /// Kept beside <see cref="ScriptRanToCompletion"/> and separated from it deliberately: the script
    /// can run to completion AND still have learned nothing about a rule. Both are "we did not
    /// establish this", and both must produce Unknown rather than a verdict.
    /// </remarks>
    internal static bool AnyRuleUnreadable(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Trim().StartsWith(RuleFailurePrefix, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Marks a rule whose lookup failed for a reason other than absence.</summary>
    internal const string RuleFailurePrefix = "RULEFAIL|";

    /// <summary>The last line the script emits, proving it reached the end rather than died midway.</summary>
    internal const string CompletionSentinel = "REMEX-FW-DONE";

    /// <summary>Whether the query script ran to completion.</summary>
    /// <remarks>
    /// Separated so the distinction that matters — "ran and found nothing" versus "did not run" — is
    /// testable on any operating system, rather than only inside a Windows-gated branch.
    /// </remarks>
    internal static bool ScriptRanToCompletion(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            if (line.Trim() == CompletionSentinel) return true;
        }

        return false;
    }

    /// <summary>
    /// Base64-UTF16LE, the encoding <c>-EncodedCommand</c> expects.
    /// </summary>
    /// <remarks>
    /// Chosen over escaping because escaping has to be right for every character the script could
    /// ever contain, while encoding is right by construction. The failure this prevents was silent:
    /// a mis-quoted script does not throw, it produces a parse error inside PowerShell and a
    /// non-zero exit, which this class correctly reads as "could not check" — so the row degrades to
    /// permanently amber and looks like a cautious design rather than a bug.
    /// </remarks>
    private static string EncodeCommand(string script) =>
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    /// <summary>Parses the <c>RULE|name|enabled|allows|program</c> lines emitted by the script above.</summary>
    /// <remarks>
    /// Split with a count limit so a program path containing the delimiter cannot shift the fields.
    /// A Windows path cannot contain <c>|</c>, but the parser should not be the thing that depends
    /// on that.
    /// <para>
    /// NO LINE-LEVEL TRIM, and its absence is deliberate. One was written here and then removed for
    /// the same reason the ufw comment-skipping branch in <see cref="FirewallReadiness"/> was: no
    /// mutant could kill it. The output is CRLF, so the stray carriage return lands at the END of
    /// the line — inside the program field — where <see cref="NormalizeProgramScope"/> trims it
    /// anyway, and nothing this script emits has leading whitespace. A guard that cannot change any
    /// answer reads to a later maintainer as though it can.
    /// </para>
    /// </remarks>
    internal static List<WindowsFirewallRule> ParseWindowsRules(string output)
    {
        var rules = new List<WindowsFirewallRule>();

        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split('|', 5);
            if (fields.Length < 5 || fields[0] != "RULE") continue;

            rules.Add(new WindowsFirewallRule(
                Name: fields[1],
                Enabled: fields[2] == "1",

                // '?' is the script's marker for an action that is neither Allow nor Block.
                Allows: fields[3] switch { "1" => true, "0" => false, _ => (bool?)null },
                ProgramPath: NormalizeProgramScope(fields[4])));
        }

        return rules;
    }

    /// <summary>
    /// Turns the firewall's program field into "the path" or "no scope at all".
    /// </summary>
    /// <remarks>
    /// "ANY" IS THE FIREWALL'S WORD FOR UNSCOPED, AND IT IS A STRING, NOT AN ABSENCE.
    /// <c>Get-NetFirewallApplicationFilter</c> returns the literal <c>Any</c> for a rule that is not
    /// program-scoped — never null, never empty. Review caught this: the null check downstream could
    /// therefore never fire from a real query, and the path comparison instead compared "Any"
    /// against the agent's executable, failed, and reported "the firewall is refusing inbound
    /// traffic on 5005" for a rule that permits everything.
    ///
    /// Compared case-insensitively because it is a Windows-supplied token, not a path.
    /// </remarks>
    internal static string? NormalizeProgramScope(string field)
    {
        var trimmed = field.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Equals("Any", StringComparison.OrdinalIgnoreCase)) return null;

        return trimmed;
    }

    /// <remarks>
    /// firewalld is asked first because it can actually answer: its read-only queries are available
    /// over D-Bus to an unprivileged process, which is what the agent is on Linux. ufw is consulted
    /// second and can only ever say "not filtering" or "cannot tell" — see
    /// <see cref="FirewallReadiness.InterpretUfw"/>.
    ///
    /// NEITHER PRESENT IS UNKNOWN, NOT OK. A machine can filter with nftables or iptables directly,
    /// with no manager installed, and this process cannot see that. Reporting green because we found
    /// nothing to ask would be a guess, and the guess would be wrong on precisely the hand-configured
    /// machines whose owners are least likely to suspect their own firewall.
    /// </remarks>
    private bool? QueryLinux(int port)
    {
        var state = _run("firewall-cmd", "--state");

        if (state.Ran)
        {
            var query = _run("firewall-cmd", $"--query-port={port}/tcp");
            var verdict = FirewallReadiness.InterpretFirewalld(
                state.ExitCode,
                query.Ran ? query.ExitCode : int.MinValue);

            if (verdict is not null) return verdict;
        }

        return FirewallReadiness.InterpretUfw(_readFile("/etc/ufw/ufw.conf"));
    }

    private static CommandResult RunCommand(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null) return CommandResult.NotRun;

            // BOTH PIPES ARE DRAINED ON BACKGROUND THREADS, AND THE TIMEOUT DEPENDS ON IT. The first
            // version called StandardOutput.ReadToEnd() before WaitForExit while stderr was
            // redirected and never read, which is the classic deadlock: once the child fills the
            // stderr pipe buffer it blocks, so it never closes stdout, so ReadToEnd never returns —
            // and the five-second timeout below is never reached, because control never gets there.
            // Review reproduced it: a child writing 200 KB to stderr left ReadToEnd blocked
            // indefinitely. That is a readiness check hanging the card it exists to populate.
            //
            // Event-based rather than ReadToEndAsync so this stays honestly synchronous — the repo
            // treats sync-over-async as a deadlock source in its own right (docs/ASYNC_GUIDELINES.md).
            var output = new System.Text.StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (output) output.AppendLine(e.Data);
            };

            // Read and discarded. Stderr carries nothing this class can use, but a pipe nobody
            // empties is a pipe that fills.
            process.ErrorDataReceived += (_, _) => { };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Bounded, because a readiness check must not become the thing that hangs the card. A
            // firewall query that has not answered in five seconds is not going to.
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                TryKill(process);
                return CommandResult.NotRun;
            }

            // The parameterless overload after a successful timed wait is what flushes the async
            // readers; without it the last lines can still be in flight when the builder is read.
            //
            // It is itself unbounded, which is a real if unreachable caveat rather than an oversight:
            // the process has already exited, so this only waits for stdout EOF, and EOF is delayed
            // only if a GRANDCHILD inherited the handle and outlived its parent. Neither
            // powershell.exe nor firewall-cmd does that. If a future command might, this needs a
            // different shape rather than a longer timeout.
            process.WaitForExit();

            lock (output) return new CommandResult(true, process.ExitCode, output.ToString());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or PlatformNotSupportedException or IOException)
        {
            // Not installed, not on PATH, or refused. All of them mean the same thing to the caller:
            // no fact was obtained. Deliberately not a rethrow — SystemReadinessService would treat
            // a throw as Unknown too, but only after abandoning the timing of the remaining checks.
            return CommandResult.NotRun;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to kill. Nothing useful remains to do about it.
        }
    }

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
