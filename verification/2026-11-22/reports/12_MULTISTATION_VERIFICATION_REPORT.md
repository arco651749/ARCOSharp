# 模組驗證報告 12：`MULTISTATION`（12 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/MULTISTATION/*.csvi`
- **測試程式**：[MultiStationModuleVerificationTests.cs](../tests/MultiStationModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/MULTISTATION/`（目前已產出 13 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 12 / 12 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `AddStation.csvi` | `MultiStation@AddStation :: station=FeedInStation {items=[入料;加工;檢驗;出料]}`<br>`MultiStation@AddStation :: station=TrayA {slots=20}` | PASS | [AddStation.png](../screenshots/MULTISTATION/AddStation.png) |
| 2 | `ChkStationRunState.csvi` | `MultiStation@ChkStationRunState :: station=FeedInStation.加工` | PASS | [ChkStationRunState.png](../screenshots/MULTISTATION/ChkStationRunState.png) |
| 3 | `FindEmpty.csvi` | `MultiStation@FindEmpty :: station=OutTray -> var=DstSlot, index=DstIdx` | PASS | [FindEmpty.png](../screenshots/MULTISTATION/FindEmpty.png) |
| 4 | `FindFirstOK.csvi` | `MultiStation@FindFirstOK :: station=InTray -> var=SrcSlot, index=SrcIdx` | PASS | [FindFirstOK.png](../screenshots/MULTISTATION/FindFirstOK.png) |
| 5 | `GetData.csvi` | `MultiStation@GetData :: station=FeedInStation.加工 {field=SN} -> var=CurSN` | PASS | [GetData.png](../screenshots/MULTISTATION/GetData.png) |
| 6 | `ResetDefault.csvi` | `MultiStation@ResetDefault {station=FeedInStation}` | PASS | [ResetDefault.png](../screenshots/MULTISTATION/ResetDefault.png) |
| 7 | `SetData.csvi` | `MultiStation@SetData :: station=FeedInStation.入料, val=SN001 {field=SN}` | PASS | [SetData.png](../screenshots/MULTISTATION/SetData.png) |
| 8 | `SetOK.csvi` | `MultiStation@SetOK :: station=FeedInStation.檢驗, logic=Cam0#OK? & PressCurve#OK?`<br>`MultiStation@SetOK :: station=TrayA.3, logic=False` | PASS | [SetOK.png](../screenshots/MULTISTATION/SetOK.png) |
| 9 | `SetOn.csvi` | `MultiStation@SetOn :: station=FeedInStation.入料, logic=InSensor?`<br>`MultiStation@SetOn :: station=FeedInStation.出料, logic=False` | PASS | [SetOn.png](../screenshots/MULTISTATION/SetOn.png) |
| 10 | `StateShift.csvi` | `MultiStation@StateShift {station=FeedInStation, shift=1}`<br>`MultiStation@StateShift {station=FeedInStation, shift=-1}` | PASS | [StateShift.png](../screenshots/MULTISTATION/StateShift.png) |
| 11 | `Transfer.csvi` | `MultiStation@Transfer :: from=FeedInStation.出料, to=OutTray.3` | PASS | [Transfer.png](../screenshots/MULTISTATION/Transfer.png) |
| 12 | `UpdateIndicator.csvi` | `MultiStation@UpdateIndicator {station=FeedInStation}` | PASS | [UpdateIndicator.png](../screenshots/MULTISTATION/UpdateIndicator.png) |
