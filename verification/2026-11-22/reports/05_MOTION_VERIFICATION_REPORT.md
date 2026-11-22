# 模組驗證報告 05：`MOTION`（21 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/MOTION/*.csvi`
- **測試程式**：[MotionModuleVerificationTests.cs](../tests/MotionModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/MOTION/`（目前已產出 22 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 21 / 21 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `AddMotion.csvi` | `Motion@AddMotion :: mot=Motor1, driver=MockMotion {device=COM3, axis=4, speed=100}` | PASS | [AddMotion.png](../screenshots/MOTION/AddMotion.png) |
| 2 | `AddPath.csvi` | `Motion@AddPath :: mot=Motor1, speed=100, pt=WayPt1`<br>`Motion@AddPath :: mot=Motor1, speed=100, pt=(150,80,20,0,0,0)` | PASS | [AddPath.png](../screenshots/MOTION/AddPath.png) |
| 3 | `ClearPath.csvi` | `Motion@ClearPath :: mot=Motor1` | PASS | [ClearPath.png](../screenshots/MOTION/ClearPath.png) |
| 4 | `Config.csvi` | `MOTION@Config` | PASS | [Config.png](../screenshots/MOTION/Config.png) |
| 5 | `ExtraCMD.csvi` | `MOTION@ExtraCMD` | PASS | [ExtraCMD.png](../screenshots/MOTION/ExtraCMD.png) |
| 6 | `GetPos.csvi` | `Motion@GetPos :: mot=Motor1` | PASS | [GetPos.png](../screenshots/MOTION/GetPos.png) |
| 7 | `GetState.csvi` | `MOTION@GetState` | PASS | [GetState.png](../screenshots/MOTION/GetState.png) |
| 8 | `Go(SingleAxis).csvi` | `MOTION@Go(SingleAxis)` | PASS | [Go(SingleAxis).png](../screenshots/MOTION/Go(SingleAxis).png) |
| 9 | `Go.csvi` | `Motion@Go(SingleAxis) :: mot=Motor1, axis=Z, speed=50, coords=10` | PASS | [Go.png](../screenshots/MOTION/Go.png) |
| 10 | `GoPallet.csvi` | `Motion@GoPallet :: mot=Motor1, speed=100, pt=Tray2D` | PASS | [GoPallet.png](../screenshots/MOTION/GoPallet.png) |
| 11 | `GoPoint(SingleAxis).csvi` | `MOTION@GoPoint(SingleAxis)` | PASS | [GoPoint(SingleAxis).png](../screenshots/MOTION/GoPoint(SingleAxis).png) |
| 12 | `GoPoint.csvi` | `Motion@GoPoint(SingleAxis) :: mot=Motor1, speed=80, pt=PickPos`<br>`Motion@GoPoint(SingleAxis) :: mot=Motor1, speed=80, pt=(inf,inf,50)` | PASS | [GoPoint.png](../screenshots/MOTION/GoPoint.png) |
| 13 | `GoSafe(Pallet).csvi` | `MOTION@GoSafe(Pallet)` | PASS | [GoSafe(Pallet).png](../screenshots/MOTION/GoSafe(Pallet).png) |
| 14 | `GoSafe.csvi` | `Motion@GoSafe(Pallet) :: mot=Motor1, speed=100, pt=Tray2D {axis=Z, safePos=20}` | PASS | [GoSafe.png](../screenshots/MOTION/GoSafe.png) |
| 15 | `Home.csvi` | `Motion@Home :: mot=Motor1 {axis=Z, mode=Positive}`<br>`Motion@Home :: mot=Robot1 {mode=ResetCounter}` *(axis Robot 可省略；mode 固定值：`ResetCounter` / `Positive` / `Negative`)*` | PASS | [Home.png](../screenshots/MOTION/Home.png) |
| 16 | `RunPath.csvi` | `Motion@RunPath :: mot=Motor1` | PASS | [RunPath.png](../screenshots/MOTION/RunPath.png) |
| 17 | `SetTool.csvi` | `MOTION@SetTool` | PASS | [SetTool.png](../screenshots/MOTION/SetTool.png) |
| 18 | `Stop.csvi` | `Motion@Stop {mot=Motor1}` | PASS | [Stop.png](../screenshots/MOTION/Stop.png) |
| 19 | `WaitMotion(Error).csvi` | `MOTION@WaitMotion(Error)` | PASS | [WaitMotion(Error).png](../screenshots/MOTION/WaitMotion(Error).png) |
| 20 | `WaitMotion.csvi` | `Motion@WaitMotion(Error) :: mot=Motor1 {timeout=10}` | PASS | [WaitMotion.png](../screenshots/MOTION/WaitMotion.png) |
| 21 | `WaitPosition.csvi` | `MOTION@WaitPosition` | PASS | [WaitPosition.png](../screenshots/MOTION/WaitPosition.png) |
