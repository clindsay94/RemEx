---
name: telemetry
description: "Skill for the Telemetry area of RemEx. 21 symbols across 4 files."
---

# Telemetry

21 symbols | 4 files | Cohesion: 95%

## When to Use

- Working with code in `Remex.Host/`
- Understanding how WindowsTelemetryService, LinuxTelemetryService, GetTelemetryAsync work
- Modifying telemetry-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | GetTelemetryAsync, MapChipName, MapLabel, InferCategory, GetCpuUsageAsync (+4) |
| `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | GetTelemetryAsync, TryReadHwInfo, FormatSensorValue, NormalizeUnit, DetermineCategory (+4) |
| `Remex.Core/Services/ITelemetryService.cs` | GetTelemetryAsync, ITelemetryService |
| `Remex.Host/Services/Telemetry/TelemetryBackgroundService.cs` | ExecuteAsync |

## Entry Points

Start here when exploring this area:

- **`WindowsTelemetryService`** (Class) — `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs:17`
- **`LinuxTelemetryService`** (Class) — `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs:13`
- **`GetTelemetryAsync`** (Method) — `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs:104`
- **`GetTelemetryAsync`** (Method) — `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs:77`
- **`ITelemetryService`** (Interface) — `Remex.Core/Services/ITelemetryService.cs:10`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsTelemetryService` | Class | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 17 |
| `LinuxTelemetryService` | Class | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 13 |
| `ITelemetryService` | Interface | `Remex.Core/Services/ITelemetryService.cs` | 10 |
| `GetTelemetryAsync` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 104 |
| `GetTelemetryAsync` | Method | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 77 |
| `MapChipName` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 214 |
| `MapLabel` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 236 |
| `InferCategory` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 259 |
| `GetCpuUsageAsync` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 280 |
| `GetRamUsageAsync` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 312 |
| `GetUptimeAsync` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 333 |
| `ParseMeminfoLine` | Method | `Remex.Host/Services/Telemetry/LinuxTelemetryService.cs` | 348 |
| `GetTelemetryAsync` | Method | `Remex.Core/Services/ITelemetryService.cs` | 15 |
| `TryReadHwInfo` | Method | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 121 |
| `FormatSensorValue` | Method | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 224 |
| `NormalizeUnit` | Method | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 238 |
| `DetermineCategory` | Method | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 245 |
| `ExecuteAsync` | Method | `Remex.Host/Services/Telemetry/TelemetryBackgroundService.cs` | 20 |
| `ReadWmiFallback` | Method | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 271 |
| `GetUptime` | Method | `Remex.Host/Services/Telemetry/WindowsTelemetryService.cs` | 354 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `ExecuteAsync → FormatSensorValue` | intra_community | 4 |
| `ExecuteAsync → NormalizeUnit` | intra_community | 4 |
| `ExecuteAsync → DetermineCategory` | intra_community | 4 |
| `ExecuteAsync → ParseMeminfoLine` | cross_community | 4 |
| `ExecuteAsync → GetCpuUsageAsync` | cross_community | 3 |
| `ExecuteAsync → GetUptimeAsync` | cross_community | 3 |
| `ExecuteAsync → MapChipName` | cross_community | 3 |

## How to Explore

1. `gitnexus_context({name: "WindowsTelemetryService"})` — see callers and callees
2. `gitnexus_query({query: "telemetry"})` — find related execution flows
3. Read key files listed above for implementation details
