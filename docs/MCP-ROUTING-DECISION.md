# MCP routing: decision record

**Bead:** RemEx-56fu.6 · **Decided:** 2026-08-24 · **Shape:** bundle-and-route · **Status:** implemented

## What was asked

Whether to fold the three MCP servers — `token-savior`, `gitnexus`, `context-mode` — into
one thing this repo owns. Three shapes were on the table at filing:

| Shape | Verdict |
|---|---|
| **Bundle-and-route** — a router skill plus hooks plus the existing servers declared in a repo `.mcp.json`. Forks nothing. | **Chosen.** |
| **Facade server** — an MCP server that is itself a client to the three, exposing ~8 tools instead of ~200. | Rejected. Breaks on any upstream tool rename, and the schema-count argument it rests on is already paid down by ToolSearch deferral. |
| **True merge** — fork three indexers (one Python, two Node, three index formats). | Rejected. Weeks of work, and upstream updates are lost permanently. |

## The measurement changed the job

The spike was going to quantify staleness across "three separate indexes over one repo."
That premise turned out to be dead. Measured 2026-08-24 in a live session:

- `claude mcp list` reported **one** of the three connected: `plugin:context-mode:context-mode`.
- `ToolSearch` for `mcp__token-savior__find_symbol`, `mcp__gitnexus__impact` and
  `mcp__gitnexus__context` returned *"No matching deferred tools found"*. Neither server
  appeared anywhere in the session's deferred tool list.
- Neither was in `~/.claude.json` (token-savior: 0 occurrences, gitnexus: 1) nor in
  `plugins/installed_plugins.json`.

So there were not three indexes drifting. There were two servers that had silently stopped
existing, and a set of instructions that went on mandating eight of their tools.

### Root cause

Their config entries were wiped out of `~/.claude.json`. Nothing principled happened; the
entries just vanished.

Recovered from the `~/.claude.json.tmp.*` snapshots Claude Code leaves behind on every
write, which preserve a timeline:

```
top-level mcpServers             -> gitnexus      (user scope)
projects["Z:/RemEx"].mcpServers  -> token-savior  (local scope)
```

Present and identical in every snapshot from 2026-08-05 through 2026-08-11 06:16, the
newest that survives. Both empty in `.claude.json.backup` (2026-08-20) and in the live
file. 135 real `mcp__token-savior__*` / `mcp__gitnexus__*` tool-use records across 37
session transcripts, newest 2026-08-15. **So the wipe happened between 2026-08-15 and
2026-08-20.** No snapshot survives inside that window, so the triggering event is not
recoverable from disk. (Claude Code was updated around 2026-08-22, but that is after the
wipe and cannot be the cause.)

Nothing was wrong with the installs. `token-savior.exe` was present the whole time and
`gitnexus` responded on PATH at 1.6.6-rc.114.

### Why it took nine days to notice

Two things hid it.

**The hooks kept firing.** `PreToolUse` kept emitting `[GitNexus] N related symbols
found`; `PostToolUse` kept emitting `[token-savior:capture] Bash output NNNNB sandboxed
to ts://capture/NNNN`. The banner said the system was live. The tool surface was gone.

**The decoy config.** `~/.claude/settings.json` carries its own `mcpServers` block —
`fetch`, `filesystem`, `memory`, `sequential-thinking`, `token-savior`, `gitnexus`,
`android-adb` — and has since 2026-06-05. **Claude Code does not read it.** Anyone
auditing by opening `settings.json`, which is the obvious thing to do, concludes the
servers are configured.

That is proved rather than assumed, by a natural experiment already in the data: gitnexus
was listed in *both* files. It worked while `~/.claude.json` had it and stopped the moment
that entry was wiped, even though the `settings.json` entry never changed.

## The pattern this belongs to

This is the third instance of one shape in this repo:

1. **`memory-store`**, retired 2026-08-09 — the server could not connect while its skills and SessionStart banner still claimed it was live.
2. **The GitNexus block in `AGENTS.md`** — drifted until it instructed agents to do the exact opposite of what the code did.
3. **This.**

In all three the system reported health it did not have, and in all three the instructions
outlived the capability. `CLAUDE.md`'s own precedence section names the cost: *"A rule
nobody can follow is worse than no rule: it teaches agents that this file's MUSTs are
decorative, which discounts the ones that are load-bearing."*

**A check that compares MANDATED tools against CALLABLE tools would have caught all three.**
That, rather than consolidation, turned out to be the deliverable worth building.

## What shipped

| Artifact | Purpose |
|---|---|
| `.mcp.json` | Repo-owned server definitions, so the set is version-controlled and reproducible instead of living only in user-scope config no other machine or worktree reproduces. Defaults are the known-good Windows paths, byte-identical to the entries that work; `REMEX_GITNEXUS_BIN` / `REMEX_TOKEN_SAVIOR_BIN` override on CachyOS. |
| `.claude/skills/mcp-routing/SKILL.md` | The routing policy, as a skill that gets loaded rather than a table the model is trusted to remember. Keeps the read-structurally / edit-literally rule intact — it is correct and it is the part agents most often get wrong. |
| `scripts/check-mcp-health.ps1` | Mandated-vs-callable check. Verifies each server resolves on disk, is defined where Claude Code actually reads, and that the gitnexus index is not behind HEAD. Flags the `settings.json` decoy explicitly. `-Full` adds a real `claude mcp list` liveness probe. |
| `scripts/check-mcp-health.sh` | Linux entry point. A shim to the same script, not a second implementation — a second implementation would recreate the exact drift this bead is about. |
| `.claude/settings.json` | `SessionStart` hook runs the check in `-Hook -Quick` mode. Never blocks a session. |

Duplicated copies of the routing matrix were removed from `CLAUDE.md` and
`docs/ralph-board-drain.md` and replaced with pointers to the skill.

`context-mode` is deliberately **not** in `.mcp.json`: it is a user-scope plugin
(`plugin:context-mode:context-mode`), not a repo-owned server.

## Scope note, recorded honestly

This is harness machinery, not RemEx product. The standing preference is that machinery
beads get deferred rather than drained, so usage goes to the product. This one was a
deliberate exception because the doc/reality gap was actively misinforming every session.
The exception covered the repair; the plugin build was judged on its own merits after the
measurement, and came out much smaller than the half-day estimate at filing.

## Open items

- **The decoy block is still in `~/.claude/settings.json`.** Removing it is a global-config
  edit outside this repo and was left for the operator. The health check warns about it on
  every session until it is gone. Delete the `mcpServers` key; do not maintain it.
- **`memory-store@claude-plugin` is still in `installed_plugins.json`**, though it is set to
  `false` in `enabledPlugins`, so it is disabled rather than half-present. Uninstall to
  close it out.
