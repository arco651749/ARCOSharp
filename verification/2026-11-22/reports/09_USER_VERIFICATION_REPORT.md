# 模組驗證報告 09：`USER`（21 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/USER/*.csvi`
- **測試程式**：[UserModuleVerificationTests.cs](../tests/UserModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/USER/`（目前已產出 22 張 PNG）
- **總體結果**：**VM 實跑 PASS** ｜ **Guide UI (`Manifest.Schema` + `AXAML`) 21 / 21 PASS**

---

## 指令與 Guide UI 驗證明細

| # | CSVI 檔案 | v2.0 標準範例 | Guide UI (`Schema` / `AXAML`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- |
| 1 | `ButtonBlinking.csvi` | `User@ButtonBlinking :: ui=BtnAlarm, logic=Alarm?` | PASS | [ButtonBlinking.png](../screenshots/USER/ButtonBlinking.png) |
| 2 | `ButtonText.csvi` | `User@ButtonText :: ui=BtnStart, text="運行中"` | PASS | [ButtonText.png](../screenshots/USER/ButtonText.png) |
| 3 | `ButtonTrigger.csvi` | `User@ButtonTrigger :: ui=BtnStart, logic=True` | PASS | [ButtonTrigger.png](../screenshots/USER/ButtonTrigger.png) |
| 4 | `ButtonValue.csvi` | `User@ButtonValue :: ui=BtnAuto, logic=False` | PASS | [ButtonValue.png](../screenshots/USER/ButtonValue.png) |
| 5 | `DisableControl.csvi` | `User@DisableControl :: ui=BtnRun` | PASS | [DisableControl.png](../screenshots/USER/DisableControl.png) |
| 6 | `EnableControl.csvi` | `User@EnableControl :: ui=[BtnRun;BtnStop]` | PASS | [EnableControl.png](../screenshots/USER/EnableControl.png) |
| 7 | `GetControlValue.csvi` | `User@GetControlValue :: ui=TxtSpeed -> var=CurSpeed` | PASS | [GetControlValue.png](../screenshots/USER/GetControlValue.png) |
| 8 | `HideControl.csvi` | `User@HideControl :: ui=BtnDebug` | PASS | [HideControl.png](../screenshots/USER/HideControl.png) |
| 9 | `Param2UserPanel.csvi` | `User@Param2UserPanel` | PASS | [Param2UserPanel.png](../screenshots/USER/Param2UserPanel.png) |
| 10 | `QueryControls.csvi` | `User@QueryControls {kind=Button} -> var=BtnList` | PASS | [QueryControls.png](../screenshots/USER/QueryControls.png) |
| 11 | `SetControlValue.csvi` | `User@SetControlValue :: ui=TxtSpeed, val=150` | PASS | [SetControlValue.png](../screenshots/USER/SetControlValue.png) |
| 12 | `ShowControl.csvi` | `User@ShowControl :: ui=[BtnStart;LblStatus]` | PASS | [ShowControl.png](../screenshots/USER/ShowControl.png) |
| 13 | `SwitchUserPanel.csvi` | `User@SwitchUserPanel :: panel=StationA` | PASS | [SwitchUserPanel.png](../screenshots/USER/SwitchUserPanel.png) |
| 14 | `TabTrigger.csvi` | `User@TabTrigger :: ui=MainTabs, page=Settings` | PASS | [TabTrigger.png](../screenshots/USER/TabTrigger.png) |
| 15 | `TabValue.csvi` | `User@TabValue :: ui=MainTabs, page=1` | PASS | [TabValue.png](../screenshots/USER/TabValue.png) |
| 16 | `TextBoxBlinking.csvi` | `User@TextBoxBlinking :: ui=TxtMsg, logic=False` | PASS | [TextBoxBlinking.png](../screenshots/USER/TextBoxBlinking.png) |
| 17 | `TextBoxTrigger.csvi` | `User@TextBoxTrigger :: ui=TxtLot, text="LOT-001"` | PASS | [TextBoxTrigger.png](../screenshots/USER/TextBoxTrigger.png) |
| 18 | `TextBoxValue.csvi` | `User@TextBoxValue :: ui=TxtMsg, text="Ready"` | PASS | [TextBoxValue.png](../screenshots/USER/TextBoxValue.png) |
| 19 | `TextRingBlinking.csvi` | `User@TextRingBlinking :: ui=CmbMode, logic=True` | PASS | [TextRingBlinking.png](../screenshots/USER/TextRingBlinking.png) |
| 20 | `TextRingTrigger.csvi` | `User@TextRingTrigger :: ui=CmbMode, val=Manual` | PASS | [TextRingTrigger.png](../screenshots/USER/TextRingTrigger.png) |
| 21 | `TextRingValue.csvi` | `User@TextRingValue :: ui=CmbMode, val=Auto` | PASS | [TextRingValue.png](../screenshots/USER/TextRingValue.png) |
