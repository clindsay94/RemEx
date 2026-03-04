# AGENT_COMMS.md — Shared Communication Log

> **Purpose**: This is the single source of truth for inter-agent communication. Every agent **must** read this file before starting work and write to it when they have findings, decisions, fixes, disputes, or status updates. The Orchestrator (user) monitors this file to track all project activity.

---

## 📋 Format Rules

Every entry must follow this format:

```
### [DATE] [TYPE] — From: [AGENT] → To: [AGENT or ALL]
**Severity**: 🔴 Critical | 🟠 High | 🟡 Medium | 🔵 Low | ⚪ Info
**Message**: [What happened or what was found]
**Reasoning**: [Brief, plain-English explanation of WHY — written so a non-technical orchestrator can understand the thinking]
**Action Required**: [What needs to happen next, and who should do it — or "None"]
```

**Entry Types**:

- `FINDING` — QA Agent discovered an issue
- `FIX` — Fix Agent resolved an issue
- `DECISION` — System Architect made an architectural decision
- `DISPUTE` — An agent disagrees with another agent's approach
- `BUILD` — Build & Deploy Agent reporting build/release status
- `INFO` — General status update or advisory note

**Rules**:

- Entries are **reverse-chronological** (newest on top)
- **Never delete entries** — this is an append-only log
- **Never edit another agent's entries** — add a new entry referencing the original
- Keep reasoning concise but informative — 1–3 sentences

---

## 🔴 Active Issues

| # | Date | Severity | Issue | Filed By | Assigned To | Status |
|---|------|----------|-------|----------|-------------|--------|
| — | —    | —        | No open issues | — | — | — |

---

## ⚖️ Dispute Log

| # | Date | Between | Issue | Arbiter Decision | Resolved? |
|---|------|---------|-------|-----------------|-----------|
| — | —    | —       | No disputes on record | — | — |

---

## 📝 Communication Log

### 2026-03-04 BUILD — From: Build & Deploy Agent → To: ALL

**Severity**: ⚪ Info
**Message**: Full solution build verified. `dotnet build Remex.sln` — 0 errors, 2 pre-existing NuGet warnings (NU1510 in Remex.Host, unrelated). All 13 unit tests pass (5 existing + 8 new DashboardProfile/SnapToGrid tests).
**Reasoning**: Cross-platform build verification is required after every structural change. All new files compile cleanly across the shared Remex.Client project.
**Action Required**: Manual testing of drag/resize/snap-to-grid behavior once app is launched.

### 2026-03-04 INFO — From: Android Platform Agent → To: ALL

**Severity**: ⚪ Info
**Message**: Advisory review complete. The `HostAddress` persistence fix (moved from volatile `ConnectionViewModel` property to `DashboardProfile` saved via `DashboardLayoutService`) will resolve the Android IP memory loss issue. `DashboardLayoutService` uses `Environment.SpecialFolder.LocalApplicationData` which maps correctly to Android internal storage.
**Reasoning**: Android's Activity lifecycle destroys and recreates the app state. Persisting settings to JSON in LocalApplicationData ensures they survive lifecycle transitions.
**Action Required**: None — fix is included in this implementation.

### 2026-03-04 DECISION — From: System Architect → To: ALL

**Severity**: ⚪ Info
**Message**: Approved architecture for Canvas Dashboard Overhaul — Command Center Edition. Replacing WrapPanel dashboard with: (1) HomeView — NOC-style landing page with pinned sensors in UniformGrid, (2) CanvasView — free-form Canvas workspace with draggable/resizable cards, staging drawer, snap-to-grid, (3) SettingsView — snap toggle, grid size, persisted host address. Navigation via ShellViewModel + TransitioningContentControl (CrossFade). IP address persistence fixes Android connection memory loss. Full plan in implementation_plan.md.
**Reasoning**: User requested a highly interactive, custom NOC-style dashboard. Canvas with pointer-event dragging and Thumb resizing provides native-feeling interaction. TransitioningContentControl keeps navigation "on-glass" for consistent cross-platform scaling. Moving HostAddress to persisted profile solves the Android settings loss issue.
**Action Required**: All agents review implementation_plan.md. Testing Agent to prepare test scaffolding for DashboardProfile and snap-to-grid math. Android Platform Agent to advisory review touch UX implications.
