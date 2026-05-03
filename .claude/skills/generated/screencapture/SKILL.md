---
name: screencapture
description: "Skill for the ScreenCapture area of RemEx. 42 symbols across 5 files."
---

# ScreenCapture

42 symbols | 5 files | Cohesion: 87%

## When to Use

- Working with code in `Remex.Host/`
- Understanding how WindowsScreenCaptureService, LinuxScreenCaptureService, CaptureScreenAsync work
- Modifying screencapture-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Services/ScreenCapture/DxgiDesktopCapture.cs` | QueryInterface, Release, InitializeDuplication, TryCapture, CaptureInternal (+11) |
| `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | CaptureScreenAsync, CaptureWaylandAsync, CaptureWithSpectacleAsync, CaptureX11Async, CaptureWithFfmpegAsync (+9) |
| `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | CaptureScreenAsync, GetJpegEncoder, GetSystemMetrics, GetCursorInfo, GetIconInfo (+5) |
| `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | MockScreenCaptureService |
| `Remex.Core/Services/IScreenCaptureService.cs` | IScreenCaptureService |

## Entry Points

Start here when exploring this area:

- **`WindowsScreenCaptureService`** (Class) — `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs:14`
- **`LinuxScreenCaptureService`** (Class) — `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs:12`
- **`CaptureScreenAsync`** (Method) — `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs:42`
- **`CaptureScreenAsync`** (Method) — `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs:51`
- **`TryCapture`** (Method) — `Remex.Host/Services/ScreenCapture/DxgiDesktopCapture.cs:311`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsScreenCaptureService` | Class | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 14 |
| `LinuxScreenCaptureService` | Class | `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 12 |
| `IScreenCaptureService` | Interface | `Remex.Core/Services/IScreenCaptureService.cs` | 5 |
| `CaptureScreenAsync` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 42 |
| `CaptureScreenAsync` | Method | `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 51 |
| `TryCapture` | Method | `Remex.Host/Services/ScreenCapture/DxgiDesktopCapture.cs` | 311 |
| `Dispose` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 142 |
| `Dispose` | Method | `Remex.Host/Services/ScreenCapture/DxgiDesktopCapture.cs` | 569 |
| `MockScreenCaptureService` | Class | `Remex.Host.Tests/RemoteDesktopHandlerTests.cs` | 41 |
| `GetJpegEncoder` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 146 |
| `GetSystemMetrics` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 162 |
| `GetCursorInfo` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 195 |
| `GetIconInfo` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 198 |
| `DrawIconEx` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 201 |
| `DeleteObject` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 207 |
| `DrawCursorOnBitmap` | Method | `Remex.Host/Services/ScreenCapture/WindowsScreenCaptureService.cs` | 216 |
| `CaptureWaylandAsync` | Method | `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 110 |
| `CaptureWithSpectacleAsync` | Method | `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 142 |
| `CaptureX11Async` | Method | `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 181 |
| `CaptureWithFfmpegAsync` | Method | `Remex.Host/Services/ScreenCapture/LinuxScreenCaptureService.cs` | 223 |

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
