---
name: controls
description: "Skill for the Controls area of RemEx. 55 symbols across 7 files."
---

# Controls

55 symbols | 7 files | Cohesion: 92%

## When to Use

- Working with code in `remex.desktop/`
- Understanding how ToggleCardSelection, BringToFront, OnCardDropped work
- Modifying controls-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.desktop/Controls/DraggableCard.cs` | OnPointerPressed, EnterDragMode, OnPointerMoved, OnPointerReleased, CancelLongPress (+7) |
| `remex.desktop/Controls/ColorPickerPopup.cs` | BuildUI, SvPad_PointerPressed, SvPad_PointerMoved, UpdateSvFromPointer, UpdateColor (+6) |
| `remex.desktop/Controls/ZoomableCanvas.cs` | OnPointerPressed, StartMultiTouchGesture, UpdateMultiTouchGesture, GetTwoPointerPositions, Distance (+6) |
| `remex.desktop/Controls/VirtualCursorPad.cs` | HitTest, OnPointerPressed, OnPointerMoved, SendNudge, RepeatNudgeAsync (+3) |
| `remex.desktop/Controls/SparklineControl.cs` | OnPropertyChanged, UpdateBrushes, Render, RenderBars, RenderLine (+1) |
| `remex.desktop/ViewModels/CanvasDashboardViewModel.cs` | ToggleCardSelection, BringToFront, OnCardDropped, OnCardResized |
| `remex.desktop/Controls/BootSequenceControl.cs` | Render, DrawText, CubicBezier |

## Entry Points

Start here when exploring this area:

- **`ToggleCardSelection`** (Method) — `remex.desktop/ViewModels/CanvasDashboardViewModel.cs:263`
- **`BringToFront`** (Method) — `remex.desktop/ViewModels/CanvasDashboardViewModel.cs:500`
- **`OnCardDropped`** (Method) — `remex.desktop/ViewModels/CanvasDashboardViewModel.cs:510`
- **`OnCardResized`** (Method) — `remex.desktop/ViewModels/CanvasDashboardViewModel.cs:565`
- **`Render`** (Method) — `remex.desktop/Controls/SparklineControl.cs:145`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `ToggleCardSelection` | Method | `remex.desktop/ViewModels/CanvasDashboardViewModel.cs` | 263 |
| `BringToFront` | Method | `remex.desktop/ViewModels/CanvasDashboardViewModel.cs` | 500 |
| `OnCardDropped` | Method | `remex.desktop/ViewModels/CanvasDashboardViewModel.cs` | 510 |
| `OnCardResized` | Method | `remex.desktop/ViewModels/CanvasDashboardViewModel.cs` | 565 |
| `Render` | Method | `remex.desktop/Controls/SparklineControl.cs` | 145 |
| `ResetView` | Method | `remex.desktop/Controls/ZoomableCanvas.cs` | 274 |
| `PanTo` | Method | `remex.desktop/Controls/ZoomableCanvas.cs` | 286 |
| `Render` | Method | `remex.desktop/Controls/VirtualCursorPad.cs` | 143 |
| `Render` | Method | `remex.desktop/Controls/BootSequenceControl.cs` | 158 |
| `OnPointerPressed` | Method | `remex.desktop/Controls/DraggableCard.cs` | 132 |
| `EnterDragMode` | Method | `remex.desktop/Controls/DraggableCard.cs` | 195 |
| `OnPointerMoved` | Method | `remex.desktop/Controls/DraggableCard.cs` | 232 |
| `OnPointerReleased` | Method | `remex.desktop/Controls/DraggableCard.cs` | 287 |
| `CancelLongPress` | Method | `remex.desktop/Controls/DraggableCard.cs` | 329 |
| `OnResizeDragCompleted` | Method | `remex.desktop/Controls/DraggableCard.cs` | 353 |
| `FindCanvasDashboard` | Method | `remex.desktop/Controls/DraggableCard.cs` | 363 |
| `FindZoomableCanvas` | Method | `remex.desktop/Controls/DraggableCard.cs` | 376 |
| `BuildUI` | Method | `remex.desktop/Controls/ColorPickerPopup.cs` | 60 |
| `SvPad_PointerPressed` | Method | `remex.desktop/Controls/ColorPickerPopup.cs` | 163 |
| `SvPad_PointerMoved` | Method | `remex.desktop/Controls/ColorPickerPopup.cs` | 168 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnPointerReleased → SaveInternalAsync` | cross_community | 7 |
| `OnPointerReleased → Dispose` | cross_community | 6 |
| `OnPointerReleased → UpdateUndoRedoState` | cross_community | 5 |
| `OnPointerReleased → RemoveCardOperation` | cross_community | 4 |

## Connected Areas

| Area | Connections |
|------|-------------|
| ViewModels | 3 calls |
| Services | 1 calls |

## How to Explore

1. `gitnexus_context({name: "ToggleCardSelection"})` — see callers and callees
2. `gitnexus_query({query: "controls"})` — find related execution flows
3. Read key files listed above for implementation details
