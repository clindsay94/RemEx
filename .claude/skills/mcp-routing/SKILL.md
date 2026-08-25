---
name: mcp-routing
description: Use when deciding how to look at code in RemEx - which of Read, Grep, Glob, token-savior, gitnexus or context-mode to reach for. Covers the read-structurally / edit-literally rule, what each server is actually for, and what to do when a mandated tool turns out not to be callable. Invoke before the first file lookup of a task, and whenever an MCP tool errors or goes missing.
---

# MCP routing in RemEx

Three MCP servers exist here to keep bulk bytes out of context. They are for
**understanding** code. They are not a substitute for reading a file you are
about to change.

This used to be a prose table in `CLAUDE.md` that the model was trusted to
remember. It is a skill now because remembering a table is not the same as
loading one, and because the table was wrong for nine days without anyone
noticing (see *When the tools are not there*, below).

## The one rule that matters

**Read structurally. Edit literally.**

| You are about to | Use | Not |
|---|---|---|
| **Understand** what code does, what calls it, where a concept lives | symbol and graph tools | a whole-file `Read`, a repo-wide `grep` |
| **Edit** a specific file | `Read` it first, then `Edit` | editing bytes you have not seen |
| **Observe** a short fixed output (`git status`, `command -v x`) | plain `Bash` | `ctx_execute` - routing three lines through a sandbox costs more than it saves |
| **Process** output you will filter, count or aggregate | `ctx_execute` | raw output into context |
| **Mutate** state (`git`, `mv`, `rm`, installs, `verify.ps1`) | plain `Bash`/`PowerShell` | `ctx_execute` - its sandbox filesystem is discarded, so writes and builds done there do not exist |

`Read` is an **edit-time** tool here, not a discovery tool. `Edit` matches against
exact bytes held in context, so an unread file cannot be edited. That is not an
exception to the rule, it is the rule.

The mistake agents actually make is the reverse of the one the rule guards
against: they read structurally *and then edit blind*, or they `Read` a
2000-line file to answer "what calls this". Both are failures of the same rule.

## Which server for what

### `token-savior` - symbol-level retrieval
Replaces reading whole files. Reach for it first; it is the cheapest rung.

- `find_symbol` - where is this class/method/function
- `get_function_source` - one function's body, nothing else
- `get_edit_context` - the surrounding lines you need to make an edit
- `get_dependents` / `get_dependencies` - callers and callees, without tracing imports by hand
- `search_codebase`, `search_in_symbols` - when you know the shape but not the name

### `gitnexus` - the knowledge graph
Precomputed call graph and execution flows. Use it for questions that span files.

- `impact` - blast radius before a **cross-cutting** edit
- `context` - full 360° on one symbol in a single turn
- `query` - process-grouped execution flows, instead of brute-force keyword search
- `detect_changes` - advisory, what a diff actually touched

**When `impact` is required, honestly stated.** The managed block in `CLAUDE.md`
says *never* edit any symbol without it. That is unfollowable and `CLAUDE.md`'s
own precedence section overrides it. It applies to:

- a symbol with callers outside its own file
- anything named in `docs/REGRESSION-GUARDS.md`
- any signature or contract change

It does **not** apply to test files, new symbols, localization, comments, or
single-call-site private helpers.

`impact` is informational, never a veto. HIGH or CRITICAL means proceed
carefully and say so - it does not mean stop. In a headless `/ralph` or `/drain`
iteration there is no user to warn, so record the risk in the bead and the
journal instead.

**Check freshness before trusting it.** The graph answers from its last index. If
it is behind HEAD it will answer confidently from a dead snapshot without
mentioning that. `gitnexus status` tells you; `gitnexus analyze` fixes it.

### `context-mode` - sandboxed execution
For output you intend to process rather than observe.

- `ctx_execute` / `ctx_execute_file` - write code to count, filter, parse, aggregate. Only what you `console.log` enters context.
- `ctx_batch_execute` - several commands in parallel, auto-indexed, with queries answered in the same round trip
- `ctx_search` - BM25 over everything already captured, including session memory
- `ctx_fetch_and_index` - instead of `WebFetch`

Its sandbox filesystem is **discarded**. File writes go through `Write`/`Edit`.
Builds, `scripts/verify.ps1`, git and installs go through plain `Bash`/`PowerShell`.

## The retrieval ladder

Go in order. Do not skip to a lower rung.

1. `memory_search` / `bd memories` - has this been solved or decided before?
2. `find_symbol`, `get_function_source`, `get_edit_context` - targeted symbol retrieval
3. `context`, `impact`, `detect_changes` - structural questions
4. `Grep` / `Glob` - only when the target is not a symbol: config, prose, resx, XAML text
5. `Read` a whole file - last resort for discovery; **first resort before an edit**

Speculative greps are the main source of context noise. Prefer one precise call
over three exploratory ones.

## When the tools are not there

This is the part worth loading. RemEx has now shipped the same failure three
times, and each time it presented as confidence rather than as an error:

- **memory-store**, retired 2026-08-09 - its skills and SessionStart banner kept claiming it was live after the server stopped connecting.
- **The GitNexus block in `AGENTS.md`** - drifted until it instructed agents to do the opposite of what the code did.
- **gitnexus and token-savior**, 2026-08-15 to 2026-08-20 - their config entries were wiped out of `~/.claude.json`. Their `PreToolUse`/`PostToolUse` hooks kept firing, so the banner still said the system was live. The tool surface was gone for nine days.

The third one was invisible because **two files claim to define MCP servers and
Claude Code only reads one**. `~/.claude/settings.json` carries an `mcpServers`
block that is inert and always has been. It is the file a human is most likely
to open.

**So: if a tool this skill mandates returns "No matching deferred tools found",
that is a broken harness, not a reason to guess.** Do this, in order:

1. `pwsh scripts/check-mcp-health.ps1 -Full` - it compares mandated against callable and names what is wrong.
2. Fall back to `Read`/`Grep`/`Glob` for the immediate task, and **say out loud** that you are doing so. Silent degradation is how this lasted nine days.
3. File a bead. Do not just work around it.

Do not audit this by reading `~/.claude/settings.json`. Read `.mcp.json` in the
repo root - that is the version-controlled definition - and confirm liveness with
`claude mcp list`.

## Related

- `Z:\RemEx\.mcp.json` - the repo-owned server definitions, and the Linux overrides
- `scripts/check-mcp-health.ps1` - mandated-vs-callable check, also wired as a SessionStart hook
- `CLAUDE.md` - precedence section, which outranks the managed blocks below it
- `docs/REGRESSION-GUARDS.md` - the guards `impact` is mandatory for
