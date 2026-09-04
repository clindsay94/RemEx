# CLAUDE.md

This file covers **Claude-harness wiring only** — precedence over the tool-managed blocks, MCP
routing, memory ownership, and issue tracking.

## 📖 Read `AGENTS.md` first

**[`AGENTS.md`](AGENTS.md) holds the project rules** — architecture invariants, the Hard Rules,
build commands, the `scripts/verify.ps1` verification gate, coding conventions, cross-platform
parity, and UI verification axes. It is the authority on anything that is not Claude-harness-specific.

RemEx is developed with Claude Code only, as of 2026-08-09. The two-file split is no longer about
serving other vendors' agents — it is now just separation of concerns: **rules** in `AGENTS.md`,
**harness wiring** here. Do not reintroduce Codex/Gemini/Cursor/Antigravity workflows or
compatibility caveats; there is nothing left to be compatible with.

Those rules used to be duplicated here. They are not any more — **if a rule is missing from this
file, it is in `AGENTS.md`, not absent.** Do not copy them back: two copies drift, and the last time
they did, this repo shipped an instruction telling agents to do the exact opposite of what the code
does.

## ⚖️ Precedence — this section outranks the managed blocks below

Roughly half of this file (everything from `<!-- gitnexus:start -->` onward) is **generated and
overwritten by `bd` and `gitnexus`**. Those blocks cannot be corrected in place; the tools rewrite
them on their next sync, and the beads block is content-hashed. So the corrections live here.

**Where this section and a managed block disagree, this section wins.** Specifically:

| Managed block says | Actually |
|---|---|
| "Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files" | Scoped, not absolute. See *Memory ownership* below. The harness-injected `MEMORY.md` is legitimate and is not what that rule is aimed at. |
| "NEVER edit a function, class, or method without first running `gitnexus_impact`" | Applies to **cross-cutting edits** — a symbol with callers outside its own file, anything named in `docs/REGRESSION-GUARDS.md`, or a signature/contract change. Not to test files, new symbols, localization, comments, or single-call-site private helpers. |
| "MUST warn the user if impact analysis returns HIGH or CRITICAL" | In a headless `/ralph` or `/drain` iteration there is no user. Record the risk in the bead and the journal instead. |
| "NEVER commit changes without running `gitnexus_detect_changes()`" | `scripts/verify.ps1 -Check` is the commit gate (`AGENTS.md`). `detect_changes` is advisory on top of it, not a second gate. |
| "Conservative (default): Do not run git commits … unless explicitly asked" / "Do not commit or push without clear authority" | **Committing is standing-authorized.** When work is done and the verify gate passes, commit it that turn (conventional prefix + bead ID) without asking — Connor decided 2026-09-02 that ending a turn with an uncommitted, verified change is the wrong default. Pushing and Dolt remote sync still wait for an explicit ask. |

A rule nobody can follow is worse than no rule: it teaches agents that this file's MUSTs are
decorative, which discounts the ones that are load-bearing. If you find another unfollowable
instruction, fix it here rather than obeying it or silently ignoring it.

## 🛑 MCP server routing — read structurally, edit literally

Three MCP servers — `token-savior`, `gitnexus`, `context-mode` — exist to keep bulk bytes out of
context. They are for **understanding** code. They are not a substitute for reading a file you are
about to change.

**The rule, stated so it can actually be followed:**

- **Understanding** what code does, what calls it, where a concept lives → symbol and graph tools.
  Never a whole-file `Read`, never a repo-wide `grep`.
- **Editing** a file → `Read` it first. `Edit` matches against exact bytes held in context, so an
  unread file cannot be edited. This is not an exception to the rule, it is the rule: `Read` is an
  edit-time tool here, not a discovery tool.
- **Observing** a short, fixed output (`git status`, `command -v x`) → plain `Bash`. Routing a
  three-line result through a sandbox costs more than it saves.
- **Processing** output you intend to filter, count, or aggregate → `ctx_execute`.
- **Mutating** state (`git`, `mv`, `rm`, installs, `scripts/verify.ps1`) → plain `Bash`/`PowerShell`.
  `ctx_execute` discards its sandbox filesystem, so writes and builds performed there do not exist.

### The per-tool matrix lives in a skill, not here

Which server for which job — `token-savior` symbol retrieval, `gitnexus` graph and
`impact`, `context-mode` sandboxed execution, the retrieval ladder, and when `impact` is
genuinely required — is **[`.claude/skills/mcp-routing/SKILL.md`](.claude/skills/mcp-routing/SKILL.md)**.
Invoke it before the first file lookup of a task.

It moved there deliberately (RemEx-56fu.6). A table in this file is a table the model is
trusted to remember; a skill is one that gets loaded. More importantly, this repo keeps
getting bitten by the same thing — two copies of a claim drift until one is lying — and
the MCP matrix had already drifted into mandating eight tools that could not be called.
**Do not copy the matrix back here.** One authoritative copy is the point.

### The servers are version-controlled now

Definitions live in **[`.mcp.json`](.mcp.json)** at the repo root, not only in user-scope
config that no other machine or worktree reproduces. Linux overrides are documented in
that file.

Verify with **`pwsh scripts/check-mcp-health.ps1 -Full`**. It compares what the
instructions MANDATE against what is actually CALLABLE, and a `-Hook -Quick` pass runs at
every SessionStart. This exists because between 2026-08-15 and 2026-08-20 `gitnexus` and
`token-savior` were wiped out of `~/.claude.json` and nobody noticed for nine days: their
hooks kept firing, so the capability looked present while the tool surface was gone.

**When auditing MCP config, do not open `~/.claude/settings.json`.** It carries an
`mcpServers` block that Claude Code does not read and never has. It is inert, it is the
file a human reaches for first, and it is why that outage was invisible.

## Memory ownership

Five stores have accumulated. Two are authoritative; the rest are convenience or history. When they
disagree, read down this table and stop at the first hit.

| Store | Owns | Write to it when |
|---|---|---|
| `bd` (beads + `bd remember`) | **Authoritative** for issues, decisions, and project/technical knowledge | Always, for anything a future session must act on |
| `AGENTS.md` + `docs/REGRESSION-GUARDS.md` | **Authoritative** for rules and invariants | A rule changed; guards only ever by hand |
| Harness auto-memory (`~/.claude/projects/Z--RemEx/memory/`) | User preferences and environment facts *about Connor's machine* | A preference or env fact, never a project rule |
| `.remember/` | Session-continuity narrative, append-only | Automatic; do not hand-curate |
| `token-savior` / `context-mode` indexes | Derived caches | Never directly — they are rebuilt |

The managed beads block says "do NOT use MEMORY.md files". Read that as: **do not invent new
markdown task or knowledge files.** It is not a prohibition on the harness-injected auto-memory,
which is a different mechanism and is correctly scoped to prefs and environment facts.

`memory-store` (MCP + plugin) was retired on 2026-08-09: the server could not connect, while its
skills and its SessionStart banner still claimed it was live. Do not reinstate it without a reason.

## Regression Guards

**Read [`docs/REGRESSION-GUARDS.md`](docs/REGRESSION-GUARDS.md) before touching capture, the remote-desktop stream or its pacing, the Android H.264 decoder, SurfaceView zoom/pan, pairing and trust, or the session guard.**

Every rule in that file exists because breaking it reintroduced a real failure that presented as *silence* — a black screen, a dead stream, a bricked pairing — with no exception and no log line pointing back at the cause. Code review does not catch these; the file is the institutional memory.

It is hand-maintained and anchored to `file:line`. It replaced an auto-generated block in `AGENTS.md` that drifted out of sync with the code and, in one case, instructed agents to do the exact opposite of what the code does. **Do not regenerate it, and do not copy its guards back into `AGENTS.md` or here** — one authoritative copy is the entire point.

## Beads Issue Tracking (`bd`)

Beads is the task tracker for this repo. It replaces TODO lists, markdown task files, and ad-hoc notes entirely.

**Mandatory workflow:**
1. `bd create` — file an issue **before** writing any code
2. `bd update <id> --claim` — claim it when you start
3. `bd close <id>` — close it when done (before reporting complete)

**Rules:**
- NEVER use TodoWrite, TaskCreate, or markdown TODO lists.
- NEVER say "done" without running `bd close` on completed issues.
- Priority scale: 0=critical, 1=high, 2=medium, 3=low, 4=backlog.

<!-- agent-team:start -->
## Agent Team & Communication

Global instructions: `~/.claude/AGENTS.md` (symlink to `~/.agents/AGENTS.md`) — project-agnostic environment facts and workflow rules only; this file wins on anything RemEx-specific.

**Project-level coordination:**
- `AGENTS.md` in this repo — cross-agent rules, distilled carry-forward regression guards, beads workflow. There are NO sub-project AGENTS.md files; do not go looking for them.
- Archived 2.0-era history: `docs/OLD DOCS/AGENTS-2.0-archive.md`.
<!-- agent-team:end -->

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (28258 symbols, 62062 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> If any GitNexus tool warns the index is stale, run `npx gitnexus analyze` in terminal first.

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `gitnexus_impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `gitnexus_detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `gitnexus_query({query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `gitnexus_context({name: "symbolName"})`.

## Never Do

- NEVER edit a function, class, or method without first running `gitnexus_impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `gitnexus_rename` which understands the call graph.
- NEVER commit changes without running `gitnexus_detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/RemEx/context` | Codebase overview, check index freshness |
| `gitnexus://repo/RemEx/clusters` | All functional areas |
| `gitnexus://repo/RemEx/processes` | All execution flows |
| `gitnexus://repo/RemEx/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |

<!-- gitnexus:end -->


<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:970c3bf2 -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

**Architecture in one line:** issues live in a local Dolt DB; sync uses `refs/dolt/data` on your git remote; `.beads/issues.jsonl` is a passive export. See https://github.com/gastownhall/beads/blob/main/docs/SYNC_CONCEPTS.md for details and anti-patterns.

## Agent Context Profiles

The managed Beads block is task-tracking guidance, not permission to override repository, user, or orchestrator instructions.

- **Conservative (default)**: Use `bd` for task tracking. Do not run git commits, git pushes, or Dolt remote sync unless explicitly asked. At handoff, report changed files, validation, and suggested next commands.
- **Minimal**: Keep tool instruction files as pointers to `bd prime`; use the same conservative git policy unless active instructions say otherwise.
- **Team-maintainer**: Only when the repository explicitly opts in, agents may close beads, run quality gates, commit, and push as part of session close. A current "do not commit" or "do not push" instruction still wins.

## Session Completion

This protocol applies when ending a Beads implementation workflow. It is subordinate to explicit user, repository, and orchestrator instructions.

1. **File issues for remaining work** - Create beads for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **Handle git/sync by active profile**:
   ```bash
   # Conservative/minimal/default: report status and proposed commands; wait for approval.
   git status

   # Team-maintainer opt-in only, unless current instructions forbid it:
   git pull --rebase
   bd dolt push
   git push
   git status
   ```
5. **Hand off** - Summarize changes, validation, issue status, and any blocked sync/commit/push step

**Critical rules:**
- Explicit user or orchestrator instructions override this Beads block.
- Do not commit or push without clear authority from the active profile or the current user request.
- If a required sync or push is blocked, stop and report the exact command and error.
<!-- END BEADS INTEGRATION -->
