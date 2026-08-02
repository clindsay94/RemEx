namespace Remex.Agent.Services.Readiness;

/// <summary>
/// One Windows Defender Firewall rule, reduced to the four facts that decide the row.
/// </summary>
/// <param name="Name">
/// The rule's <c>Name</c>, which is its INVARIANT IDENTIFIER — not its <c>DisplayName</c>.
/// <para>
/// This distinction is the whole reason the Windows branch is safe to ship in nine languages.
/// <c>autostart-remex.ps1</c> creates its rules with <c>-Name RemexHostInbound</c> /
/// <c>-Name RemexClientInbound</c> and <c>-DisplayName "RemEx"</c>. The Name is chosen by us and
/// never translated; the DisplayName is what the Windows UI shows and what localized
/// <c>netsh advfirewall show rule</c> output prints. Querying by Name means the check behaves
/// identically on a German or Turkish Windows, which matching display text would not.
/// </para>
/// </param>
/// <param name="Enabled">Whether the rule is switched on. A disabled rule permits nothing.</param>
/// <param name="Allows">
/// True for <c>Action = Allow</c>, false for <c>Action = Block</c>, null for anything else.
/// <para>
/// THREE-VALUED BECAUSE THE ACTION ENUM IS. Allow and Block are both real states for a rule carrying
/// our name — a user or a management tool can flip an existing rule rather than deleting it — but
/// <c>NotConfigured</c> also exists and turns up on rules sourced from Group Policy. Folding it into
/// "block" would report "the firewall is refusing inbound traffic" on a domain machine because of a
/// rule that refuses nothing.
/// </para>
/// </param>
/// <param name="ProgramPath">
/// The executable the rule is scoped to, or null if the rule is not program-scoped. Our rules are
/// created with <c>-Program "$exePath"</c>, so the path is load-bearing rather than incidental.
/// </param>
public sealed record WindowsFirewallRule(string Name, bool Enabled, bool? Allows, string? ProgramPath);

/// <summary>
/// Turns raw firewall query facts into "is inbound allowed?" (RemEx-ksbm).
/// </summary>
/// <remarks>
/// <para>
/// SEPARATED FROM THE QUERYING ON PURPOSE, for the same reason the readiness judgements were split
/// from the readiness probes in RemEx-gpe3: shelling out is thin and untestable, while the
/// interpretation is subtle and is the part that misleads someone when it is wrong. Every method
/// here is pure.
/// </para>
/// <para>
/// WHY THIS ROW MATTERS MORE THAN THE OTHERS. A green port-listening row means THE SERVER IS UP, not
/// THE PHONE CAN REACH IT — the firewall sits between them. Without this row the card can show
/// all-green on a machine the phone cannot reach, which is exactly the support case the card exists
/// to prevent.
/// </para>
/// <para>
/// WHAT A TRUE ANSWER DOES AND DOES NOT MEAN, stated here so the localized text cannot overclaim.
/// True means "no firewall configuration we can see refuses this program". It is not a proof of
/// reachability: a router, a second host-based filter, or a rule scoped to a network profile the
/// machine is not currently on can each still stop the phone.
/// </para>
/// </remarks>
public static class FirewallReadiness
{
    /// <summary>The rule names <c>autostart-remex.ps1</c> creates. Invariant, never localized.</summary>
    /// <remarks>
    /// Exposed as a read-only list rather than an array: an array is publicly mutable in place, and
    /// a caller that rewrote these names would silently redirect the whole Windows check at rules
    /// the installer never created.
    /// </remarks>
    public static IReadOnlyList<string> WindowsRuleNames { get; } = ["RemexHostInbound", "RemexClientInbound"];

    /// <summary>
    /// Whether the Windows rules permit inbound traffic to <paramref name="agentExecutablePath"/>.
    /// </summary>
    /// <param name="rules">
    /// Every rule found under <see cref="WindowsRuleNames"/>. An EMPTY LIST MEANS THE QUERY RAN AND
    /// FOUND NOTHING, which is a definite "no" — the caller must return null instead of calling this
    /// when the query itself failed, or a broken query would be reported as a broken firewall.
    /// </param>
    /// <param name="agentExecutablePath">The executable actually running, to compare rules against.</param>
    /// <returns>True if allowed, false if refused, null if the facts do not settle it.</returns>
    public static bool? InterpretWindows(
        IReadOnlyList<WindowsFirewallRule> rules,
        string? agentExecutablePath)
    {
        if (rules is null) return null;

        // NO RULE AT ALL IS A DEFINITE NO, not an unknown. Windows denies unsolicited inbound
        // traffic by default, so the absence of our rule is not missing information — it is the
        // reason the phone cannot connect. This is the state a machine lands in when the installer's
        // firewall step failed, which autostart-remex.ps1 only warns about and carries on from.
        if (rules.Count == 0) return false;

        // A disabled rule permits and refuses nothing, so it is not evidence in either direction.
        // "The rule exists" is not the question worth asking: switching a rule off is something a
        // user does through the Windows UI without deleting anything.
        var live = rules
            .Where(r => r.Enabled)
            .Select(r => (Rule: r, Covers: CoversExecutable(r, agentExecutablePath)))
            .ToList();

        // A BLOCK RULE ENDS THE ANSWER, whatever else is present, because Windows Defender Firewall
        // evaluates block ahead of allow — one enabled block defeats every allow beside it.
        //
        // QUALIFIED BY PROGRAM SCOPE, WHICH REVIEW CAUGHT MISSING. An unqualified "any block rule"
        // test applied drift-awareness to allow rules and not to block ones, so a stale block rule
        // left pointing at an old install directory — the exact drift the allow path is written to
        // survive — reported "the firewall is refusing inbound traffic" while Windows was applying
        // that rule to a file that does not run.
        if (live.Any(x => x.Rule.Allows == false && x.Covers == true)) return false;

        // A block rule we cannot place is the one thing that must not be optimistically ignored:
        // if it does cover us, the connection is refused.
        if (live.Any(x => x.Rule.Allows == false && x.Covers is null)) return null;

        // An action that is neither Allow nor Block — NotConfigured, from Group Policy — might do
        // either. Only relevant when the rule could apply to us at all.
        if (live.Any(x => x.Rule.Allows is null && x.Covers != false)) return null;

        if (live.Any(x => x.Rule.Allows == true && x.Covers == true)) return true;

        // Allow rules exist but we cannot tell whether they name this executable. Answering "no"
        // would blame the firewall for our own missing information.
        if (live.Any(x => x.Rule.Allows == true && x.Covers is null)) return null;

        // Nothing enabled covers this executable: either every rule is scoped elsewhere, or they
        // were all switched off.
        return false;
    }

    /// <summary>
    /// Whether a rule applies to <paramref name="agentExecutablePath"/>: true, false, or null when
    /// that cannot be established.
    /// </summary>
    private static bool? CoversExecutable(WindowsFirewallRule rule, string? agentExecutablePath)
    {
        // No program scope: the rule applies whatever executable serves the port, so there is
        // nothing to compare and nothing to get wrong.
        if (string.IsNullOrWhiteSpace(rule.ProgramPath)) return true;

        // Environment.ProcessPath can be null in hosting arrangements this app does not control.
        if (string.IsNullOrWhiteSpace(agentExecutablePath)) return null;

        return PathsMatch(rule.ProgramPath, agentExecutablePath);
    }

    /// <summary>
    /// Whether two Windows executable paths name the same file.
    /// </summary>
    /// <remarks>
    /// CASE-INSENSITIVE, AND THAT IS NOT A CONTRADICTION OF THE REPO RULE. CLAUDE.md forbids
    /// case-insensitive comparison for paths that must survive a case-sensitive Linux filesystem;
    /// this comparison is Windows-only by construction — it exists to match a Windows Defender
    /// Firewall rule against a Windows executable — and NTFS is case-insensitive, so an ordinal
    /// comparison here would report "the rule does not cover you" for a rule that provably does.
    ///
    /// A path that cannot be normalized is left as-is rather than being rejected: the firewall
    /// stores whatever it was given, including forms this process may not be able to resolve, and
    /// the fallback comparison can still succeed on an exact match.
    /// </remarks>
    private static bool PathsMatch(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

        static string Normalize(string path)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

            try
            {
                return Path.GetFullPath(expanded);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return expanded;
            }
        }
    }

    /// <summary>
    /// Whether firewalld permits the port, from the exit codes of two <c>firewall-cmd</c> calls.
    /// </summary>
    /// <param name="stateExitCode"><c>firewall-cmd --state</c>. Zero means firewalld is running.</param>
    /// <param name="queryPortExitCode">
    /// <c>firewall-cmd --query-port=&lt;port&gt;/tcp</c>. Zero means open, one means not open.
    /// </param>
    /// <remarks>
    /// EXIT CODES RATHER THAN OUTPUT, because firewalld's messages go through gettext and are
    /// translated on a localized system, while its exit codes are not. This is the same trap the
    /// Windows branch avoids by querying rule Name instead of DisplayName, and it is the reason
    /// neither branch reads a word of tool output.
    ///
    /// FIREWALLD NOT RUNNING IS NOT AN ANSWER HERE. It rules firewalld out, but the machine may
    /// still have nftables or iptables rules loaded directly, and the caller — not this method —
    /// decides what to do with that. Returning true would be a guess, which the bead forbids.
    /// </remarks>
    public static bool? InterpretFirewalld(int stateExitCode, int queryPortExitCode)
    {
        if (stateExitCode != 0) return null;

        return queryPortExitCode switch
        {
            0 => true,

            // firewall-cmd documents 1 as "the query is false". Any other code is a failure of the
            // call itself — a D-Bus refusal, a malformed argument, a missing zone — and answering
            // "not open" to those would report a firewall problem the user does not have.
            1 => false,
            _ => null
        };
    }

    /// <summary>
    /// Whether ufw permits the port, from the contents of <c>/etc/ufw/ufw.conf</c>.
    /// </summary>
    /// <param name="configContents">
    /// The file's text, or null if it is absent or unreadable — which is also how "ufw is not
    /// installed" arrives.
    /// </param>
    /// <remarks>
    /// THE CONFIG FILE RATHER THAN <c>ufw status</c>, because the agent cannot run that command.
    /// <c>ufw status</c> requires root and the Linux agent is an ordinary unprivileged user process
    /// by design — that is the same fact that gives Linux no elevation row at all (RemEx-gpe3). So
    /// the obvious implementation cannot succeed on a stock Ubuntu desktop no matter how it is
    /// written, and would report a permission failure as if it were a firewall verdict.
    ///
    /// WHAT IS AND IS NOT KNOWABLE WITHOUT ROOT, which is what makes this two answers and not three.
    /// <c>ufw.conf</c> is world-readable and carries <c>ENABLED=yes|no</c>, so whether ufw is
    /// FILTERING AT ALL is an ordinary fact. The rules themselves live in <c>user.rules</c>, which is
    /// root-only, so whether a specific port is permitted is not. Hence: disabled is a definite yes,
    /// and enabled is an honest Unknown rather than a guess in either direction.
    ///
    /// Guessing the other way would be worse than it looks. Reporting "allowed" for an enabled ufw
    /// would show green on exactly the machines most likely to be blocking, since a user who turned
    /// ufw on is the user whose phone cannot connect.
    /// </remarks>
    public static bool? InterpretUfw(string? configContents)
    {
        if (configContents is null) return null;

        foreach (var rawLine in configContents.Split('\n'))
        {
            var line = rawLine.Trim();

            // ANCHORED AT THE START OF A TRIMMED LINE, which is what makes a commented-out setting
            // harmless — "# ENABLED=no" does not start with "ENABLED=". That matters because the
            // shipped ufw.conf documents this key in prose directly above it, so the obvious
            // implementation, a Contains("ENABLED=no") over the whole file, reads the documentation
            // as the configuration and answers "not filtering" on a machine that is.
            //
            // An explicit skip for comments and blank lines was written here first and then removed:
            // this condition already rejects both, so the extra branch could not change any answer
            // and would have read to a later maintainer as though it could.
            if (!line.StartsWith("ENABLED=", StringComparison.OrdinalIgnoreCase)) continue;

            var value = line["ENABLED=".Length..].Trim().Trim('"');

            // Disabled means ufw is not filtering anything, which is a real answer rather than an
            // absence of one. Enabled means it is filtering by rules this process cannot read.
            if (value.Equals("no", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return null;

            // A value that is neither is a file we do not understand. Do not guess.
            return null;
        }

        return null;
    }
}
