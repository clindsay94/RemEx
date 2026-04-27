# PRD: Android UI/UX Refactor & M3 Compliance

## Problem Statement
The app deviates from Material Design 3 and Jetpack Compose best practices.

## Goals
- M3 compliance (Scaffold, Semantic tokens).
- Improved maintainability (Consolidated state).
- Fix accessibility violations.

## Implementation Plan (High Level)
1.  **Foundation**: Create `UiState` classes and `RemexHaptic` utility.
2.  **Screen Refactoring**: Iteratively refactor all screens to use `Scaffold`.
3.  **Modernization**: Implement Pull-to-Refresh and fix CameraX lifecycle.
4.  **Validation**: Visual review and Accessibility scan.
