# 模組驗證報告 01：`SYSTEM`（21 條指令）

- **測試日期**：2026-11-22
- **測試目標**：`dist/ARCOS_Runtime/MODULES/SYSTEM/*.csvi`（正式打包 Runtime）
- **測試程式**：[SystemModuleVerificationTests.cs](../tests/SystemModuleVerificationTests.cs)
- **Guide UI 截圖目錄**：`../screenshots/SYSTEM/`（22 張 PNG）
- **總體結果**：**21 / 21 指令 VM 實跑 PASS** ｜ **21 / 21 Guide UI 稽核 PASS (`V2_READY`)** ｜ **22 / 22 視覺截圖完成**

---

## 逐條指令驗證明細

| # | 指令 (`System@Action`) | v2.0 實跑腳本 | VM 實跑結果 | 狀態/副作用驗證證據 | Guide UI (`Schema` / `AXAML` / `FormatToV2`) | 截圖檔案 |
| :-: | :--- | :--- | :-: | :--- | :-: | :--- |
| 1 | `Delay` | `System@Delay :: sec=0.06;` | PASS | 非阻塞狀態機 tick 至 `Completed`，實測耗時 ≥ 40ms | PASS (`V2_READY`) | `Delay.png` |
| 2 | `Log Message` | `System@Log Message :: msg="Production Started";` | PASS | `RecordEngine["SYSTEMLOG"]` 收到含 `"Production Started"` 訊息 | PASS (`V2_READY`) | `Log Message.png` |
| 3 | `Log Error` | `System@Log Error :: msg="Vacuum Sensor Fault";` | PASS | 觸發錯誤狀態並寫入 `SYSTEMLOG` 錯誤紀錄 | PASS (`V2_READY`) | `Log Error.png` |
| 4 | `Clear Message` | `System@Clear Message;` | PASS | 成功執行並清除訊息狀態 (`Completed`) | PASS (`V2_READY`) | `Clear Message.png` |
| 5 | `Clear Error` | `System@Clear Error;` | PASS | 成功清除錯誤/EMG 狀態 (`Completed`) | PASS (`V2_READY`) | `Clear Error.png` |
| 6 | `Note` | `System@Note :: text="=== 進料段初始化 ===";` | PASS | 純註解無副作用 (`Completed`) | PASS (`V2_READY`) | `Note.png` |
| 7 | `MessageBox` | `System@MessageBox :: msg="請確認治具已鎖緊";` | PASS | 無頭模式下正常執行完成 (`Completed`) | PASS (`V2_READY`) | `MessageBox.png` |
| 8 | `WarningBox` | `System@WarningBox :: msg="料盤即將耗盡" {logic=LowTray?};` | PASS | `LowTray?=false` 與 `true` 雙分支皆正確求值完成 | PASS (`V2_READY`) | `WarningBox.png` |
| 9 | `Pause` | `System@Pause {threads=Thread1, logic=DoorOpen?};` | PASS | v2.0 `{threads, logic}` 語法正確解析與執行 | PASS (`V2_READY`) | `Pause.png` |
| 10 | `Resume` | `System@Resume {threads=Thread1};` | PASS | v2.0 `{threads}` 語法正確解析與執行 | PASS (`V2_READY`) | `Resume.png` |
| 11 | `TimerReset` | `System@TimerReset :: timer=T_Heat {target=0.05};` | PASS | `T_Heat#Timeout?` 重設為 `false` | PASS (`V2_READY`) | `TimerReset.png` |
| 12 | `TimerCheck` | `System@TimerCheck :: timer=T_Heat;` | PASS | 經過 70ms 後 `T_Heat#Timeout?` 翻轉為 `true` | PASS (`V2_READY`) | `TimerCheck.png` |
| 13 | `CounterReset` | `System@CounterReset :: counter=C_Retry {target=2};` | PASS | `C_Retry#Over?` 重設為 `false` | PASS (`V2_READY`) | `CounterReset.png` |
| 14 | `CounterCheck` | `System@CounterCheck :: counter=C_Retry;` | PASS | 連續呼叫 2 次後 `C_Retry#Over?` 翻轉為 `true` | PASS (`V2_READY`) | `CounterCheck.png` |
| 15 | `Record` | `System@Record :: rec=CT_Pick {msg=Start};` | PASS | 建立 `CT_Pick` 起點時間戳 (`Completed`) | PASS (`V2_READY`) | `Record.png` |
| 16 | `LogRecordCycleTime` | `System@LogRecordCycleTime :: rec=CT_Pick {prefix="Pick CT:"} -> var=CycleSec;` | PASS | `Variable["CycleSec"] > 0` 且 `SYSTEMLOG` 含 `"Pick CT:"` | PASS (`V2_READY`) | `LogRecordCycleTime.png` |
| 17 | `ProgramLoad` | `System@ProgramLoad :: file="<tmp>.prog";` | PASS | 成功載入暫存 `.prog` 檔 (`Completed`) | PASS (`V2_READY`) | `ProgramLoad.png` |
| 18 | `ProgramSection` | `System@ProgramSection :: section=StationA {title=Load};` | PASS | `ctx.CurrentSection` 切換為 `"StationA"` | PASS (`V2_READY`) | `ProgramSection.png` |
| 19 | `IncludeModule` | `System@IncludeModule :: module=IO;` | PASS | `Registry.HasModule("IO") == true` | PASS (`V2_READY`) | `IncludeModule.png` |
| 20 | `UserPanelOpen` | `System@UserPanelOpen :: panel=UserPanel.csvi {alias=StationB, activate=false};` | PASS | 成功解析 `{alias, activate}` 並執行 (`Completed`) | PASS (`V2_READY`) | `UserPanelOpen.png` |
| 21 | `UserPanelClose` | `System@UserPanelClose :: panel=StationB;` | PASS | 成功關閉指定面板 (`Completed`) | PASS (`V2_READY`) | `UserPanelClose.png` |
