---
name: views
description: "Skill for the Views area of RemEx. 29 symbols across 7 files."
---

# Views

29 symbols | 7 files | Cohesion: 83%

## When to Use

- Working with code in `remex.desktop/`
- Understanding how SetAlertDialog, ConfirmationDialog, ResetViewport work
- Modifying views-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.desktop/Views/RemoteDesktopView.axaml.cs` | IsPenInput, IsTouchInput, OnViewportPointerPressed, OnViewportPointerMoved, GetTwoPointerPositions (+14) |
| `remex.desktop/Views/CanvasView.axaml.cs` | OnDataContextChanged, WireMinimapControl, OnAttachedToVisualTree, OnShowSetAlertRequested, OnCanvasViewportChanged |
| `remex.desktop/ViewModels/RemoteDesktopViewModel.cs` | UpdateViewportZoom |
| `remex.desktop/Views/SetAlertDialog.axaml.cs` | SetAlertDialog |
| `remex.desktop/Views/RemoteView.axaml.cs` | OnDataContextChanged |
| `remex.desktop/Views/ConfirmationDialog.axaml.cs` | ConfirmationDialog |
| `remex.desktop/ViewModels/CanvasDashboardViewModel.cs` | UpdateMinimapViewport |

## Entry Points

Start here when exploring this area:

- **`SetAlertDialog`** (Class) — `remex.desktop/Views/SetAlertDialog.axaml.cs:7`
- **`ConfirmationDialog`** (Class) — `remex.desktop/Views/ConfirmationDialog.axaml.cs:7`
- **`ResetViewport`** (Method) — `remex.desktop/Views/RemoteDesktopView.axaml.cs:238`
- **`UpdateViewportZoom`** (Method) — `remex.desktop/ViewModels/RemoteDesktopViewModel.cs:392`
- **`UpdateMinimapViewport`** (Method) — `remex.desktop/ViewModels/CanvasDashboardViewModel.cs:162`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `SetAlertDialog` | Class | `remex.desktop/Views/SetAlertDialog.axaml.cs` | 7 |
| `ConfirmationDialog` | Class | `remex.desktop/Views/ConfirmationDialog.axaml.cs` | 7 |
| `ResetViewport` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 238 |
| `UpdateViewportZoom` | Method | `remex.desktop/ViewModels/RemoteDesktopViewModel.cs` | 392 |
| `UpdateMinimapViewport` | Method | `remex.desktop/ViewModels/CanvasDashboardViewModel.cs` | 162 |
| `IsPenInput` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 117 |
| `IsTouchInput` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 121 |
| `OnViewportPointerPressed` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 260 |
| `OnViewportPointerMoved` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 370 |
| `GetTwoPointerPositions` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 682 |
| `Distance` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 692 |
| `Midpoint` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 695 |
| `ApplyViewportTransform` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 217 |
| `ClampViewportOffset` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 246 |
| `OnViewportPointerWheel` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 588 |
| `NotifyZoomChanged` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 676 |
| `MapToRemoteCoords` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 126 |
| `ShowCursorAt` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 184 |
| `OnViewportHolding` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 347 |
| `OnViewportPointerReleased` | Method | `remex.desktop/Views/RemoteDesktopView.axaml.cs` | 496 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnViewportPointerMoved → IsPenInput` | intra_community | 3 |
| `OnViewportPointerPressed → IsPenInput` | intra_community | 3 |

## How to Explore

1. `gitnexus_context({name: "SetAlertDialog"})` — see callers and callees
2. `gitnexus_query({query: "views"})` — find related execution flows
3. Read key files listed above for implementation details
