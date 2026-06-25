---
name: screencapture
description: "Skill for the ScreenCapture area of RemEx. 42 symbols across 5 files."
---

# ScreenCapture

42 symbols | 5 files | Cohesion: 87%

## When to Use

- Working with code in `remex.agent/`
- Understanding how WindowsScreenCaptureService, LinuxScreenCaptureService, CaptureScreenAsync work
- Modifying screencapture-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.agent/Services/ScreenCapture/DxgiDesktopCapture.cs` | QueryInterface, Release, InitializeDuplication, TryCapture, CaptureInternal (+11) |
| `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | CaptureScreenAsync, CaptureWaylandAsync, CaptureWithSpectacleAsync, CaptureX11Async, CaptureWithFfmpegAsync (+9) |
| `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | CaptureScreenAsync, GetJpegEncoder, GetSystemMetrics, GetCursorInfo, GetIconInfo (+5) |
| `remex.agent.tests/RemoteDesktopHandlerTests.cs` | MockScreenCaptureService |
| `remex.core/Services/IScreenCaptureService.cs` | IScreenCaptureService |

## Entry Points

Start here when exploring this area:

- **`WindowsScreenCaptureService`** (Class) — `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs:14`
- **`LinuxScreenCaptureService`** (Class) — `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs:12`
- **`CaptureScreenAsync`** (Method) — `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs:42`
- **`CaptureScreenAsync`** (Method) — `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs:51`
- **`TryCapture`** (Method) — `remex.agent/Services/ScreenCapture/DxgiDesktopCapture.cs:311`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsScreenCaptureService` | Class | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 14 |
| `LinuxScreenCaptureService` | Class | `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 12 |
| `IScreenCaptureService` | Interface | `remex.core/Services/IScreenCaptureService.cs` | 5 |
| `CaptureScreenAsync` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 42 |
| `CaptureScreenAsync` | Method | `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 51 |
| `TryCapture` | Method | `remex.agent/Services/ScreenCapture/DxgiDesktopCapture.cs` | 311 |
| `Dispose` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 142 |
| `Dispose` | Method | `remex.agent/Services/ScreenCapture/DxgiDesktopCapture.cs` | 569 |
| `MockScreenCaptureService` | Class | `remex.agent.tests/RemoteDesktopHandlerTests.cs` | 41 |
| `GetJpegEncoder` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 146 |
| `GetSystemMetrics` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 162 |
| `GetCursorInfo` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 195 |
| `GetIconInfo` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 198 |
| `DrawIconEx` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 201 |
| `DeleteObject` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 207 |
| `DrawCursorOnBitmap` | Method | `remex.agent/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 216 |
| `CaptureWaylandAsync` | Method | `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 110 |
| `CaptureWithSpectacleAsync` | Method | `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 142 |
| `CaptureX11Async` | Method | `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 181 |
| `CaptureWithFfmpegAsync` | Method | `remex.agent/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 223 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `CaptureScreenAsync → QueryInterface` | cross_community | 6 |
| `CaptureScreenAsync → D3D11_TEXTURE2D_DESC` | cross_community | 6 |
| `CaptureScreenAsync → Release` | cross_community | 6 |
| `CaptureScreenAsync → CURSORINFO` | cross_community | 6 |
| `CaptureScreenAsync → GetCursorInfo` | cross_community | 6 |
| `StreamFramesAsync → QueryInterface` | cross_community | 5 |
| `StreamFramesAsync → Release` | cross_community | 5 |
| `CaptureInternal → Release` | cross_community | 5 |
| `CaptureScreenAsync → RunProcessAsync` | intra_community | 4 |
| `StreamFramesAsync → CURSORINFO` | cross_community | 4 |

## How to Explore

1. `gitnexus_context({name: "WindowsScreenCaptureService"})` — see callers and callees
2. `gitnexus_query({query: "screencapture"})` — find related execution flows
3. Read key files listed above for implementation details
