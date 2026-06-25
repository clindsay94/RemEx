---
name: telemetry
description: "Skill for the Telemetry area of RemEx. 21 symbols across 4 files."
---

# Telemetry

21 symbols | 4 files | Cohesion: 95%

## When to Use

- Working with code in `remex.agent/`
- Understanding how WindowsTelemetryService, LinuxTelemetryService, GetTelemetryAsync work
- Modifying telemetry-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | GetTelemetryAsync, MapChipName, MapLabel, InferCategory, GetCpuUsageAsync (+4) |
| `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | GetTelemetryAsync, TryReadHwInfo, FormatSensorValue, NormalizeUnit, DetermineCategory (+4) |
| `remex.core/Services/ITelemetryService.cs` | GetTelemetryAsync, ITelemetryService |
| `remex.agent/Services/Telemetry/TelemetryBackgroundService.cs` | ExecuteAsync |

## Entry Points

Start here when exploring this area:

- **`WindowsTelemetryService`** (Class) — `remex.agent/Services/Telemetry/WindowsTelemetryService.cs:17`
- **`LinuxTelemetryService`** (Class) — `remex.agent/Services/Telemetry/LinuxTelemetryService.cs:13`
- **`GetTelemetryAsync`** (Method) — `remex.agent/Services/Telemetry/LinuxTelemetryService.cs:104`
- **`GetTelemetryAsync`** (Method) — `remex.agent/Services/Telemetry/WindowsTelemetryService.cs:77`
- **`ITelemetryService`** (Interface) — `remex.core/Services/ITelemetryService.cs:10`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `WindowsTelemetryService` | Class | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 17 |
| `LinuxTelemetryService` | Class | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 13 |
| `ITelemetryService` | Interface | `remex.core/Services/ITelemetryService.cs` | 10 |
| `GetTelemetryAsync` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 104 |
| `GetTelemetryAsync` | Method | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 77 |
| `MapChipName` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 214 |
| `MapLabel` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 236 |
| `InferCategory` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 259 |
| `GetCpuUsageAsync` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 280 |
| `GetRamUsageAsync` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 312 |
| `GetUptimeAsync` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 333 |
| `ParseMeminfoLine` | Method | `remex.agent/Services/Telemetry/LinuxTelemetryService.cs` | 348 |
| `GetTelemetryAsync` | Method | `remex.core/Services/ITelemetryService.cs` | 15 |
| `TryReadHwInfo` | Method | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 121 |
| `FormatSensorValue` | Method | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 224 |
| `NormalizeUnit` | Method | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 238 |
| `DetermineCategory` | Method | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 245 |
| `ExecuteAsync` | Method | `remex.agent/Services/Telemetry/TelemetryBackgroundService.cs` | 20 |
| `ReadWmiFallback` | Method | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 271 |
| `GetUptime` | Method | `remex.agent/Services/Telemetry/WindowsTelemetryService.cs` | 354 |

## Execution Flows

| Flow | Type | Steps |
|------|------|-------|
| `ExecuteAsync → FormatSensorValue` | intra_community | 4 |
| `ExecuteAsync → NormalizeUnit` | intra_community | 4 |
| `ExecuteAsync → DetermineCategory` | intra_community | 4 |
| `ExecuteAsync → ParseMeminfoLine` | cross_community | 4 |
| `ExecuteAsync → GetCpuUsageAsync` | cross_community | 3 |

## How to Explore

1. `gitnexus_context({name: "WindowsTelemetryService"})` — see callers and callees
2. `gitnexus_query({query: "telemetry"})` — find related execution flows
3. Read key files listed above for implementation details
