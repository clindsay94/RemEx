---
name: controls
description: "Skill for the Controls area of RemEx. 55 symbols across 7 files."
---

# Controls

55 symbols | 7 files | Cohesion: 92%

## When to Use

- Working with code in `Remex.Client/`
- Understanding how ToggleCardSelection, BringToFront, OnCardDropped work
- Modifying controls-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Client/Controls/DraggableCard.cs` | OnPointerPressed, EnterDragMode, OnPointerMoved, OnPointerReleased, CancelLongPress (+7) |
| `Remex.Client/Controls/ColorPickerPopup.cs` | BuildUI, SvPad_PointerPressed, SvPad_PointerMoved, UpdateSvFromPointer, UpdateColor (+6) |
| `Remex.Client/Controls/ZoomableCanvas.cs` | OnPointerPressed, StartMultiTouchGesture, UpdateMultiTouchGesture, GetTwoPointerPositions, Distance (+6) |
| `Remex.Client/Controls/VirtualCursorPad.cs` | HitTest, OnPointerPressed, OnPointerMoved, SendNudge, RepeatNudgeAsync (+3) |
| `Remex.Client/Controls/SparklineControl.cs` | OnPropertyChanged, UpdateBrushes, Render, RenderBars, RenderLine (+1) |
| `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | ToggleCardSelection, BringToFront, OnCardDropped, OnCardResized |
| `Remex.Client/Controls/BootSequenceControl.cs` | Render, DrawText, CubicBezier |

## Entry Points

Start here when exploring this area:

- **`ToggleCardSelection`** (Method) — `Remex.Client/ViewModels/CanvasDashboardViewModel.cs:263`
- **`BringToFront`** (Method) — `Remex.Client/ViewModels/CanvasDashboardViewModel.cs:500`
- **`OnCardDropped`** (Method) — `Remex.Client/ViewModels/CanvasDashboardViewModel.cs:510`
- **`OnCardResized`** (Method) — `Remex.Client/ViewModels/CanvasDashboardViewModel.cs:565`
- **`Render`** (Method) — `Remex.Client/Controls/SparklineControl.cs:145`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `ToggleCardSelection` | Method | `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | 263 |
| `BringToFront` | Method | `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | 500 |
| `OnCardDropped` | Method | `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | 510 |
| `OnCardResized` | Method | `Remex.Client/ViewModels/CanvasDashboardViewModel.cs` | 565 |
| `Render` | Method | `Remex.Client/Controls/SparklineControl.cs` | 145 |
| `ResetView` | Method | `Remex.Client/Controls/ZoomableCanvas.cs` | 274 |
| `PanTo` | Method | `Remex.Client/Controls/ZoomableCanvas.cs` | 286 |
| `Render` | Method | `Remex.Client/Controls/VirtualCursorPad.cs` | 143 |
| `Render` | Method | `Remex.Client/Controls/BootSequenceControl.cs` | 158 |
| `OnPointerPressed` | Method | `Remex.Client/Controls/DraggableCard.cs` | 132 |
| `EnterDragMode` | Method | `Remex.Client/Controls/DraggableCard.cs` | 195 |
| `OnPointerMoved` | Method | `Remex.Client/Controls/DraggableCard.cs` | 232 |
| `OnPointerReleased` | Method | `Remex.Client/Controls/DraggableCard.cs` | 287 |
| `CancelLongPress` | Method | `Remex.Client/Controls/DraggableCard.cs` | 329 |
| `OnResizeDragCompleted` | Method | `Remex.Client/Controls/DraggableCard.cs` | 353 |
| `FindCanvasDashboard` | Method | `Remex.Client/Controls/DraggableCard.cs` | 363 |
| `FindZoomableCanvas` | Method | `Remex.Client/Controls/DraggableCard.cs` | 376 |
| `BuildUI` | Method | `Remex.Client/Controls/ColorPickerPopup.cs` | 60 |
| `SvPad_PointerPressed` | Method | `Remex.Client/Controls/ColorPickerPopup.cs` | 163 |
| `SvPad_PointerMoved` | Method | `Remex.Client/Controls/ColorPickerPopup.cs` | 168 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `OnPointerReleased → SaveInternalAsync` | cross_community | 7 |
| `OnCardDropped → SerializeToUtf8Bytes` | cross_community | 7 |
| `OnPointerReleased → Dispose` | cross_community | 6 |
| `OnCardDropped → Dispose` | cross_community | 6 |
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
