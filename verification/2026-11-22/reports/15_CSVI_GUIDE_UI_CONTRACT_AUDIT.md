# ARCOSharp Layer-2 Contract Audit Report — 256 CSVI Guide UIs & Engine Syntax Bridge

- **Date**: `2026-11-22`
- **Target**: `dist/ARCOS_Runtime/MODULES/` (14 Official Modules)
- **Command CSVIs Audited**: **244** (+ **14** Module Panels)
- **StandardCommandSchemas Round-Tripped**: **246**

## 1. Executive Scorecard

| Check Category | Total | Pass | Issue / Gap | Severity |
| :--- | ---: | ---: | ---: | :--- |
| C1.1 CSVI Assembly Load | 244 | 244 | 0 | P0 |
| C1.2 `IParameterGuidance` Instantiation | 244 | 244 | 0 | P0 |
| C2.1 `StandardCommandSchemas` Coverage | 244 | 244 | 0 | P1 |
| C2.2 `Manifest.Schema` Embedded | 244 | 244 | 0 | P1 |
| C2.3 Schema Triangulation (0 Drift) | 244 | 244 | 0 | P0 |
| C3.1 AXAML Valid XML | 244 | 244 | 0 | P0 |
| C3.2 `FindControl` ↔ `x:Name` Wiring | 244 | 244 | 0 | P0 |
| C3.3 Guide UI Param Keys ↔ Schema | 244 | 244 | 0 | P0 |
| C4.1 Guide UI Re-open v2.0 Parser | 244 | 244 | 0 | P1 (UX Risk) |
| C4.2 Guide UI v2.0 Output Formatting | 244 | 242 | 2 | P2 (Legacy Emit) |
| C5.1 `ModuleRegistry` Registration | 244 | 244 | 0 | P1 |
| C5.2 Schema Synthetic v2 Parse + Validate | 246 | 246 | 0 | P0 |
| C5.3 `CommandFormatter` Idempotency | 246 | 246 | 0 | P1 |
| C5.4 `NormalizeForExecution` ↔ `FormatToV2` Round-Trip | 246 | 246 | 0 | P0 |

## 2. Per-Module Breakdown

| Module | Cmd CSVIs | Load OK | Dangling `FindControl` | Schema Drift | Reopen `V2_AWARE` / `LEGACY_ONLY` | Emit `V2` / `LEGACY` | Unregistered |
| :--- | ---: | ---: | ---: | ---: | :---: | :---: | ---: |
| `SYSTEM` | 21 | 21/21 | 0 | 0 | 19 / 0 | 19 / 0 | 0 |
| `THREAD` | 31 | 31/31 | 0 | 0 | 29 / 0 | 29 / 0 | 0 |
| `VARIABLE` | 34 | 34/34 | 0 | 0 | 34 / 0 | 34 / 0 | 0 |
| `IO` | 14 | 14/14 | 0 | 0 | 14 / 0 | 14 / 0 | 0 |
| `MOTION` | 21 | 21/21 | 0 | 0 | 21 / 0 | 21 / 0 | 0 |
| `CAPTURE` | 28 | 28/28 | 0 | 0 | 28 / 0 | 28 / 0 | 0 |
| `DAQ` | 18 | 18/18 | 0 | 0 | 18 / 0 | 18 / 0 | 0 |
| `POINT&PATH` | 22 | 22/22 | 0 | 0 | 22 / 0 | 22 / 0 | 0 |
| `USER` | 21 | 21/21 | 0 | 0 | 20 / 0 | 20 / 0 | 0 |
| `PYTHON` | 10 | 10/10 | 0 | 0 | 10 / 0 | 10 / 0 | 0 |
| `REDIS` | 9 | 9/9 | 0 | 0 | 9 / 0 | 9 / 0 | 0 |
| `MULTISTATION` | 12 | 12/12 | 0 | 0 | 12 / 0 | 10 / 2 | 0 |
| `AIAGENT` | 3 | 3/3 | 0 | 0 | 2 / 0 | 2 / 0 | 0 |

## 3. Dangling `FindControl` ↔ AXAML `x:Name` Mismatches (0)

_None — all `FindControl` calls match AXAML `x:Name` definitions._

## 4. Schema Coverage & Drift Issues (0)

_None — 100% aligned._

## 5. `CommandSyntaxBridge` v2 ↔ Legacy Round-Trip Issues (0)

_None — all StandardCommandSchemas passed full parse/validate/format/bridge round-trip._

## 6. Guide UIs with Legacy-Only Re-open Parsing (0)

> When a user double-clicks a v2.0 script line (`key=val {opt=val} -> out=$x`), Guide UIs without `CommandParamParser.Parse` or `NormalizeForExecution` may fail to pre-fill fields cleanly unless the host pre-normalizes `currentParam`.

_None — all parameterized Guide UIs are v2.0 aware._

## 7. CSVIs Not Found in `ModuleRegistry.GetAllCommands("*")` (0)

_None — all CSVIs are registered._

## 8. Missing or Invalid AXAML XML (0)

_None — all AXAML files are valid XML._

## 9. Guide UIs Emitting Legacy Raw Syntax (2)

- `MULTISTATION` / `SetOK.csvi` (`MultiStation@SetOK`) — `station=FeedInStation.檢驗, logic=True" : currentParam.Trim());`
- `MULTISTATION` / `SetOn.csvi` (`MultiStation@SetOn`) — `station=FeedInStation.入料, logic=True" : currentParam.Trim());`

## 10. Guide UI Code Referencing Unknown Schema Param Keys (0)

_None — every parsed parameter key in Guide UI code exists in CommandSchema._
