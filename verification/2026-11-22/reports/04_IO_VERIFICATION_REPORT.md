# 模組驗證報告 04：`IO`（14 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/IO/*.csvi`
- **測試程式**：[IoModuleVerificationTests.cs](../tests/IoModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/IO/`（目前已產出 15 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 14 / 14 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `AddIODriver.csvi` | `IO@AddIODriver :: card=IO1, driver=MockIO {device=COM1, outStart=P0.0, outPorts=8, inStart=P1.0, inPorts=8}` | PASS | [AddIODriver.png](../screenshots/IO/AddIODriver.png) |
| 2 | `AddVirtualIO.csvi` | `IO@AddVirtualIO :: io=CycleOK? {logic=Start? & !Alarm?}` | PASS | [AddVirtualIO.png](../screenshots/IO/AddVirtualIO.png) |
| 3 | `CheckLogicOff.csvi` | `IO@CheckLogicOff` | PASS | [CheckLogicOff.png](../screenshots/IO/CheckLogicOff.png) |
| 4 | `CheckLogicOn.csvi` | `IO@CheckLogicOn` | PASS | [CheckLogicOn.png](../screenshots/IO/CheckLogicOn.png) |
| 5 | `ClrPort.csvi` | `IO@ClrPort :: io=OUT1 {holdSec=0.2}` | PASS | [ClrPort.png](../screenshots/IO/ClrPort.png) |
| 6 | `Flash(OffOn).csvi` | `IO@Flash(OffOn)` | PASS | [Flash(OffOn).png](../screenshots/IO/Flash(OffOn).png) |
| 7 | `Flash(OnOff).csvi` | `IO@Flash(OnOff)` | PASS | [Flash(OnOff).png](../screenshots/IO/Flash(OnOff).png) |
| 8 | `ForceUpdate.csvi` | `IO@ForceUpdate :: card=IO1` | PASS | [ForceUpdate.png](../screenshots/IO/ForceUpdate.png) |
| 9 | `InitIOPort.csvi` | `IO@InitIOPort :: card=IO1` | PASS | [InitIOPort.png](../screenshots/IO/InitIOPort.png) |
| 10 | `SetPort.csvi` | `IO@SetPort :: io=OUT1 {holdSec=0.5, forceUpdate=true}` | PASS | [SetPort.png](../screenshots/IO/SetPort.png) |
| 11 | `SetPortName.csvi` | `IO@SetPortName :: io=P0.0, alias=OUT1` | PASS | [SetPortName.png](../screenshots/IO/SetPortName.png) |
| 12 | `Shift.csvi` | `IO@Shift :: ios=[OUT1;OUT2;OUT3] {shift=1}` | PASS | [Shift.png](../screenshots/IO/Shift.png) |
| 13 | `WaitPortOff.csvi` | `IO@WaitPortOff` | PASS | [WaitPortOff.png](../screenshots/IO/WaitPortOff.png) |
| 14 | `WaitPortOn.csvi` | `IO@WaitPortOn` | PASS | [WaitPortOn.png](../screenshots/IO/WaitPortOn.png) |
