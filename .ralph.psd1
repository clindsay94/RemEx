@{
    # Everything project-specific the ralph board-drain machinery needs. The scripts themselves
    # are project-agnostic and live wherever the tooling lives; this file is what makes a repo
    # ralph-enabled, and its absence is how the scripts know to refuse.
    #
    # .psd1 rather than .json ON PURPOSE: scripts/verify.ps1 fingerprints '*.json' among its
    # source patterns, so a tracked .json config would invalidate every receipt each time this
    # file changed. Same reasoning as docs/ralph-state.jsonl being .jsonl.

    # Bead ids look like '<prefix>-xxxx'. The cluster planner parses them out of commit subjects.
    BeadPrefix           = 'RemEx'

    Verify               = @{
        # Relative to the repo root, in the tree being verified (integration copy or lane).
        Command          = 'scripts/verify.ps1'
        # What -Check takes to validate an existing receipt without rebuilding.
        CheckArgs        = @('-Check')
        # Scope for the one cold build a lane pays at provisioning.
        ProvisionScope   = 'dotnet'
        # Scope for the landing verify in the integration tree.
        IntegrationScope = 'all'
    }

    # A landing that touches any of these prefixes escalates a cheap-scoped landing to the full
    # suite once at the end of the batch.
    ScopeEscalationPaths = @('remex.android/', 'remex.core/')

    # Regexes (joined with '|') matched case-insensitively against changed paths. Touching a
    # match means the diff can never take the merge queue's small-mechanical review skip -
    # a reviewer verdict is always owed. Rounds toward MORE review on purpose.
    ReviewSensitivePatterns = @(
        '^remex\.core/'                                # wire format, serialization, JNI, NativeAOT
        'pair|cert|trust|pinned|consent'               # pairing and certificate code
        'elevat|manifest|privileg|\.acl|acl\.'         # elevation, manifests, ACLs
        'capture|encoder|h264|dxgi|screengrab|xrandr'  # the capture and encode path
        'Theme\.kt$|DashboardInteraction\.kt$'         # named explicitly in the procedure's step 8
    )

    # A file that exists in every fresh worktree; provisioning probes it to prove the worktree
    # materialised before paying for a build.
    ProvisionProbeFile   = 'remex.core/remex.core.csproj'

    # Paths every bead may touch concurrently - excluded from overlap planning. The first two
    # are merge=union in .gitattributes on purpose.
    AlwaysSharedPaths    = @('docs/CHANGELOG.md', 'docs/ralph-state.jsonl', '.token-savior-cache.json')

    # The changelog every landing must touch, and the journal the merge queue appends to.
    Changelog            = 'docs/CHANGELOG.md'
    Journal              = 'docs/ralph-state.jsonl'

    # Gitignored-but-required files copied into each lane at provisioning.
    LaneManifest         = 'scripts/ralph-lane-manifest.txt'

    # The loop procedure lanes follow. Procedure is the generic loop (it ships with the tooling,
    # so an absolute path is expected); ProcedureOverlay holds this project's specifics and is
    # concatenated after it into each lane's .ralph/procedure.md at provision time. Where the two
    # disagree the overlay wins - it knows the project.
    Procedure            = 'C:/Users/Connor/.claude/ralph/board-drain.md'
    ProcedureOverlay     = 'docs/ralph-board-drain.md'

    # '' means the default: a sibling of the repo root named '<repo>.lanes'.
    LanesRoot            = ''

    # Lane session defaults. RALPH_AGENT_MODEL / RALPH_AGENT_EFFORT env still win.
    Model                = 'opus'
    Effort               = ''

    Dashboard            = @{
        Enabled          = $true
        IntervalSeconds  = 5
    }
}
