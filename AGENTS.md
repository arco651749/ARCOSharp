# ARCOSharp Project Engineering & Verification Rules

## 1. Mandatory 3-Layer Verification Gate (Auto-Run Before Reporting Done)

Every agent editing code in `d:\AntiGravity\ARCOSharp` MUST automatically execute the matching verification tier before declaring a task complete or pushing to GitHub:

| Changed Scope | Required Verification Tier | One-Line Command |
| :--- | :--- | :--- |
| `src/ARCOS.Core/**`, `src/MODULES/**`, `src/ARCOS.Remoter/**`, `tests/**` | **Layer 1 + Layer 2** (All Headless VM unit tests + 14-module verification + 244 CSVI Guide UI & 246 Schema contract round-trip audit) | `& d:\AntiGravity\ARCOSharp\tools\verify_all.ps1` |
| `src/ARCOS.App/**`, `src/ARCOS.CsviStudio/**`, `src/PANELS/**`, `tools/pack_runtime.ps1` | **Layer 1 + Layer 2 + Package + Layer 3** (Full tests + repackage `dist/ARCOS_Runtime` + Golden Factory human-like UI E2E on Desktop Cockpit virtual display `DISPLAY9`) | `& d:\AntiGravity\ARCOSharp\tools\verify_all.ps1 -IncludeUi` |

### Verification Invariants
- **Layer 1 + 2 (`dotnet test ARCOS.sln`)**: Must pass with **0 failures** (including `CsviGuideUiContractTests`: `loadFail=0`, `missingGuidance=0`, `invalidXml=0`, `danglingControls=0`, `schemaDriftCount=0`, `unregistered=0`, `bridgeRtFail=0`).
- **Layer 3 (`run_golden_human_e2e.py`)**: Must run strictly on the **Desktop Cockpit virtual secondary display (`DISPLAY9`)** with **zero interference** on the user's primary monitor, and pass all automated UI + TCP 19528 read-only oracle checks (`UI-01` ~ `UI-04`, `P2-1`, `E2E_*`).
- **Stop-on-Error**: If any tier fails, diagnose root cause, apply a surgical patch (`edit_file`), and re-run until green. Never report completion with failing gates.

---

## 2. Runtime Deployment Topology Invariants (`dist/ARCOS_Runtime`)

Never regress the clean CSVI 3.0 Runtime bundle topology enforced by `tools/pack_runtime.ps1`:
- **`PANELS/`**: `UserPanel*.csvi` belong **only** under `PANELS/` (never in repo root or `MODULES/SYSTEM/`).
- **`MODULES/<Mod>/DRIVER/`**: Only hardware-driver modules (`IO`, `MOTION`, `CAPTURE`, `DAQ`) have `DRIVER/`, and driver CSVIs (`MockIO.csvi`, `MockMotion.csvi`) live directly under `DRIVER/` (no duplicate copy inside `DRIVER/Mock*/`).
- **Official vs. Dev Modules**: `MODULES/CALL` is official and included in Runtime; `MODULES/HELLO` is dev/test-only (`src/MODULES/HELLO`) and MUST remain excluded from `dist/ARCOS_Runtime`.
- **Clean Fresh Directories**: `PICTURE/` must be empty in fresh Runtime deployments; zero legacy `USERPARAMETEREDITOR/` directories.
- **Runtime Path Isolation**: `ArcosPaths.RootDirectory` and `ModuleRegistry.GetCandidateModulesRoots()` must stay strictly isolated to `AppContext.BaseDirectory` whenever running inside a deployed Runtime bundle (identified by `MODULES/SYSTEM/Delay.csvi` present and `ARCOS.sln` absent), preventing upward traversal into a parent git repo.

---

## 3. UI & Accessibility Guardrails

- **UIA Names**: Composite or icon-only Avalonia `<Button>` controls (`<StackPanel>` / `<Path>` content) MUST declare `AutomationProperties.Name="..."` so Windows UIA and Desktop Cockpit never see `"Avalonia.Controls.StackPanel"`.
- **Primary Button Hover Contrast**: Any dark-colored `<Button>` with white inner text must override `:pointerover` and `:pressed` `ContentPresenter` background (e.g., `Classes="primary-sky"` / `primary-emerald`) so Avalonia Fluent hover does not turn the button white-on-gray.
- **CSVI Syntax v2.0 Only**: All user-facing hints, defaults, and Guide UIs must emit and display ARCOS Syntax v2.0 (`key=val {opt=val} -> out=$v`).

---

## 4. Environment & CLI Quick Reference

- **.NET 8 SDK**: `& "D:\Users\arco0\.dotnet\dotnet.exe"`
- **Python UTF-8**: Always run Python scripts with `python.exe -X utf8` and `sys.stdout.reconfigure(encoding="utf-8")`.
- **CSVI_Studio Headless Capture**: Must pass **both** `--capture-target <Wizard|Panel>` AND `--capture-out <out.png>` (omitting `--capture-target` leaves the GUI open).
- **Date-Partitioned Verification Artifacts**: Store new verification suites and reports under `verification/<YYYY-MM-DD>/`.
