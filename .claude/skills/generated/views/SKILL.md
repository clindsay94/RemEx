---
name: views
description: "Skill for the Views area of RemEx. 29 symbols across 7 files."
---

# Views

29 symbols | 7 files | Cohesion: 83%

## When to Use

- Working with code in `Remex.Client/`
- Understanding how SetAlertDialog, ConfirmationDialog, ResetViewport work
- Modifying views-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Client/Views/RemoteDesktopView.axaml.cs` | IsPenInput, IsTouchInput, OnViewportPointerPressed, OnViewportPointerMoved, GetTwoPointerPositions (+14) |
| `Remex.Client/Views/CanvasView.axaml.cs` | OnDataContextChanged, WireMinimapControl, OnAttachedToVisualTree, OnShowSetAlertRequested, OnCanvasViewportChanged |
| `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | UpdateViewportZoom |
| `Remex.Client/Views/SetAlertDialog.axaml.cs` | SetAlertDialog |
| `Remex.Client/Views/RemoteView.axaml.cs` | OnDataContextChanged |
| `Remex.Client/Views/ConfirmationDialog.axaml.cs` | ConfirmationDialog |
| `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | UpdateMinimapViewport |

## Entry Points

Start here when exploring this area:

- **`SetAlertDialog`** (Class) — `Remex.Client/Views/SetAlertDialog.axaml.cs:7`
- **`ConfirmationDialog`** (Class) — `Remex.Client/Views/ConfirmationDialog.axaml.cs:7`
- **`ResetViewport`** (Method) — `Remex.Client/Views/RemoteDesktopView.axaml.cs:238`
- **`UpdateViewportZoom`** (Method) — `Remex.Client/ViewModels/RemoteDesktopViewModel.cs:392`
- **`UpdateMinimapViewport`** (Method) — `Remex.Client/ViewModels/CanvasDashboardViewModel.cs:162`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `SetAlertDialog` | Class | `Remex.Client/Views/SetAlertDialog.axaml.cs` | 7 |
| `ConfirmationDialog` | Class | `Remex.Client/Views/ConfirmationDialog.axaml.cs` | 7 |
| `ResetViewport` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 238 |
| `UpdateViewportZoom` | Method | `Remex.Client/ViewModels/RemoteDesktopViewModel.cs` | 392 |
| `UpdateMinimapViewport` | Method | `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | 162 |
| `IsPenInput` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 117 |
| `IsTouchInput` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 121 |
| `OnViewportPointerPressed` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 260 |
| `OnViewportPointerMoved` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 370 |
| `GetTwoPointerPositions` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 682 |
| `Distance` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 692 |
| `Midpoint` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 695 |
| `ApplyViewportTransform` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 217 |
| `ClampViewportOffset` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 246 |
| `OnViewportPointerWheel` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 588 |
| `NotifyZoomChanged` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 676 |
| `MapToRemoteCoords` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 126 |
| `ShowCursorAt` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 184 |
| `OnViewportHolding` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 347 |
| `OnViewportPointerReleased` | Method | `Remex.Client/Views/RemoteDesktopView.axaml.cs` | 496 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnViewportPointerMoved → IsPenInput` | intra_community | 3 |
| `OnViewportPointerPressed → IsPenInput` | intra_community | 3 |

## How to Explore

1. `gitnexus_context({name: "SetAlertDialog"})` — see callers and callees
2. `gitnexus_query({query: "views"})` — find related execution flows
3. Read key files listed above for implementation details
