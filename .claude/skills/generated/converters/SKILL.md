---
name: converters
description: "Skill for the Converters area of RemEx. 21 symbols across 3 files."
---

# Converters

21 symbols | 3 files | Cohesion: 100%

## When to Use

- Working with code in `Remex.Client.Tests/`
- Understanding how StringMatchConverter, Zero_Returns_ZeroB, OneB_Returns_OneB work
- Modifying converters-related functionality

## Key Files

| File | Symbols |
|------|---------|
| `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | Convert, Zero_Returns_ZeroB, OneB_Returns_OneB, MaxBytes_Returns_B, ExactlyOneKB_Returns_KB (+11) |
| `Remex.Client/Converters/StringMatchConverter.cs` | StringMatchConverter, StringEqualsConverter, ParameterEqualsConverter |
| `Remex.Client/Converters/BytesToHumanReadableConverter.cs` | Convert, ConvertBack |

## Entry Points

Start here when exploring this area:

- **`StringMatchConverter`** (Class) — `Remex.Client/Converters/StringMatchConverter.cs:10`
- **`Zero_Returns_ZeroB`** (Method) — `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs:16`
- **`OneB_Returns_OneB`** (Method) — `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs:17`
- **`MaxBytes_Returns_B`** (Method) — `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs:18`
- **`ExactlyOneKB_Returns_KB`** (Method) — `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs:21`

## Key Symbols

| Symbol | Type | File | Line |
|--------|------|------|------|
| `StringMatchConverter` | Class | `Remex.Client/Converters/StringMatchConverter.cs` | 10 |
| `Zero_Returns_ZeroB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 16 |
| `OneB_Returns_OneB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 17 |
| `MaxBytes_Returns_B` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 18 |
| `ExactlyOneKB_Returns_KB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 21 |
| `HalfMB_Returns_KB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 22 |
| `JustUnderOneMB_Returns_KB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 23 |
| `ExactlyOneMB_Returns_MB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 26 |
| `HalfGB_Returns_MB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 27 |
| `JustUnderOneGB_Returns_MB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 28 |
| `ExactlyOneGB_Returns_GB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 31 |
| `EightGB_Returns_GB` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 32 |
| `Int_Input_Works` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 35 |
| `Null_Returns_Dash` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 36 |
| `Negative_Returns_Dash` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 37 |
| `Convert` | Method | `Remex.Client/Converters/BytesToHumanReadableConverter.cs` | 22 |
| `ConvertBack_ThrowsNotSupported` | Method | `Remex.Client.Tests/Converters/BytesToHumanReadableConverterTests.cs` | 40 |
| `ConvertBack` | Method | `Remex.Client/Converters/BytesToHumanReadableConverter.cs` | 48 |
| `StringEqualsConverter` | Class | `Remex.Client/Converters/StringMatchConverter.cs` | 55 |
| `ParameterEqualsConverter` | Class | `Remex.Client/Converters/StringMatchConverter.cs` | 68 |

## How to Explore

1. `gitnexus_context({name: "StringMatchConverter"})` — see callers and callees
2. `gitnexus_query({query: "converters"})` — find related execution flows
3. Read key files listed above for implementation details
