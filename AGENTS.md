<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RemEx** (7061 symbols, 14541 relationships, 300 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

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
| Work in the ViewModels area (280 symbols) | `.claude/skills/generated/viewmodels/SKILL.md` |
| Work in the Screens area (200 symbols) | `.claude/skills/generated/screens/SKILL.md` |
| Work in the Native area (82 symbols) | `.claude/skills/generated/native/SKILL.md` |
| Work in the Services area (68 symbols) | `.claude/skills/generated/services/SKILL.md` |
| Work in the Command area (67 symbols) | `.claude/skills/generated/command/SKILL.md` |
| Work in the Controls area (55 symbols) | `.claude/skills/generated/controls/SKILL.md` |
| Work in the Input area (49 symbols) | `.claude/skills/generated/input/SKILL.md` |
| Work in the ScreenCapture area (42 symbols) | `.claude/skills/generated/screencapture/SKILL.md` |
| Work in the Remex area (41 symbols) | `.claude/skills/generated/remex/SKILL.md` |
| Work in the Network area (39 symbols) | `.claude/skills/generated/network/SKILL.md` |
| Work in the Handlers area (33 symbols) | `.claude/skills/generated/handlers/SKILL.md` |
| Work in the Widget area (31 symbols) | `.claude/skills/generated/widget/SKILL.md` |
| Work in the Views area (29 symbols) | `.claude/skills/generated/views/SKILL.md` |
| Work in the Security area (24 symbols) | `.claude/skills/generated/security/SKILL.md` |
| Work in the Remex.Core.Tests area (23 symbols) | `.claude/skills/generated/remex-core-tests/SKILL.md` |
| Work in the FileTransfer area (22 symbols) | `.claude/skills/generated/filetransfer/SKILL.md` |
| Work in the Converters area (21 symbols) | `.claude/skills/generated/converters/SKILL.md` |
| Work in the Telemetry area (21 symbols) | `.claude/skills/generated/telemetry/SKILL.md` |
| Work in the ProcessMonitor area (19 symbols) | `.claude/skills/generated/processmonitor/SKILL.md` |
| Work in the Remex.Client area (18 symbols) | `.claude/skills/generated/remex-client/SKILL.md` |

<!-- gitnexus:end -->
