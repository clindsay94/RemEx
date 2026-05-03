# Design Spec: RemEx 2.0 Track-Based AGENTS.md System

**Date:** 2026-05-03
**Status:** Approved by User
**Topic:** Reorganizing project documentation to support the 2.0 "Cosmic Raven" multi-agent upgrade plan.

## 1. Overview
RemEx is undergoing a major 2.0 upgrade involving high-risk security changes (TLS 1.3, pairing) and new flagship features (file transfer). This requires a distributed documentation system (`AGENTS.md`) that keeps multiple agents aligned with a centralized, phased master plan.

## 2. Design Goals
- **Alignment:** Ensure all agents follow the sequential Phase 0 -> Phase 1 -> Phase 2 flow.
- **Safety:** Prevent parallel edits to "chokepoint" files that would cause merge conflicts.
- **Verification:** Make success criteria and verification commands localized to the project being edited.
- **Intelligence:** Integrate GitNexus nodes to help agents understand the new protocol flows.

## 3. Component Specifications

### 3.1 Root `AGENTS.md` (Mission Control)
The root file serves as the strategic entry point.
- **Master Plan Reference:** Links to `2.0-Plan/master-plan.md`.
- **Coordination Rules:** Explicitly forbids editing chokepoint files (mapping included) outside of their assigned phases.
- **GitNexus Integration:** Standard CLI/MCP instructions + 2.0 anchor nodes. 
    - **MANDATORY:** Remind agents to run `gitnexus mcp` to start the MCP server if it's not responding.


### 3.2 Project-Specific `AGENTS.md` (Tactical Playbooks)
Located in `Remex.Core`, `Remex.Host`, `Remex.Client`, `Remex.Client.Desktop`, and `RemEx.Android`.
- **Project Role:** Definition of the project's responsibilities in 2.0.
- **Assigned Tracks:** List of Track IDs (e.g., `0B-message-types`) that affect this directory.
- **Critical Nodes:** High-value symbols for `gitnexus_context` (e.g., `RemexMessage`, `PairingService`).
- **Verification:** Command-line checklist for validating track completion.

## 4. Implementation Details
- **Root AGENTS.md:** New file overwriting the existing one.
- **Sub-Project AGENTS.md:** New files in the root of each major project directory.
- **Content Source:** Derived exclusively from the approved "Cosmic Raven" Master Plan.

## 5. Success Criteria
- Each major project directory contains an `AGENTS.md`.
- No agent attempts to edit `RemexMessage.cs` (Phase 0) while working on a Phase 2 track.
- Verification commands listed in `AGENTS.md` align with the Master Plan.
