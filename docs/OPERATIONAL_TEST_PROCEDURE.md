# ARCOSharp 工業級端到端全操作流測試規範與自動化回歸指南 (E2E Operational Flow Testing Guide)

本文件詳述 **ARCOSharp 工業自動化主控台** 的全操作流測試規範、架構原理、一鍵回歸測試步驟以及各階段檢驗標準，供後續開發工程師與 AI Agent 重測、驗收及排查問題時使用。

---

## 1. 測試架構與設計哲學

### 1.1 為什麼需要「全操作流」端到端測試？
在複雜的工控軟體中，僅依賴後端的無頭單元測試（`dotnet test`）存在嚴重的「無頭盲區」：
1. **測不到 Avalonia XAML 視窗生命週期**：例如父子視窗層次（Owner / Modality）、對話盒切換時的焦點遺失、隱藏視窗後的控制項脫落等問題。
2. **測不到真實 GPU 渲染與 UI 排版**：文字截斷、按鈕被擠壓、暗黑/淺色主題對比度瑕疵等。
3. **測不到真實硬體驅動與多執行緒並行交互**：如原生 C-API（Python 3.12 DLL）、動態 CSVI 反射組件、即時 I/O 點位強制更新。

### 1.2 雙輪驅動測試架構 (ADC + JSON-RPC)
本測試方案由兩個核心子系統無縫協同運作：
- **Desktop Cockpit (ADC 2.0 / 3.0)**：
  - 將被測應用程式派駐至獨立的 Windows 虛擬第二螢幕（預設座標：$X = 1920, Y = -688$）。
  - **人機零干擾**：測試全程在虛擬顯示器中運作，工程師的主螢幕**0 彈窗干擾、實體滑鼠 0 位移**。
  - 支援 DXGI 60 FPS 真實畫面截取。
- **內嵌自動化服務 (JSON-RPC 2.0 over TCP 19528)**：
  - ARCOS.App 啟動時自動於 `127.0.0.1:19528` 監聽。
  - 提供即時狀態查詢 (`arcos_get_state`)、排程編輯與單步步進 (`arcos_sequencer_action`)、視覺控制項枚舉 (`arcos_list_controls`)、點擊與輸入 (`arcos_click` / `arcos_input`)、以及高解析度對話盒截圖 (`arcos_capture_ui`)。

---

## 2. 測試環境前置要求 (Prerequisites)

| 元件 | 規格要求 | 說明 |
| :--- | :--- | :--- |
| **作業系統** | Windows 10 / 11 (x64) | 必須在 Windows 原生環境下執行。 |
| **執行架構** | .NET 8.0 SDK / Runtime | 核心應用程式與 CSVI 虛擬儀表運行基底。 |
| **原生 Python** | Python 3.12 (64-bit) | 系統需裝有 `python312.dll`（預設搜尋 `C:\Python312` 或 `PATH`）。 |
| **虛擬顯示器** | USBMMIDD 虛擬螢幕驅動 | 虛擬第二螢幕 `DISPLAY9` 需處於啟用狀態。 |
| **Cockpit CLI** | `cockpit_cli.py` | 位於外掛目錄或專案工具路徑。 |

---

## 3. 一鍵自動化重測方式 (Quick Start)

在 Windows PowerShell 終端機下直接執行持久化測試腳本：

```powershell
# 1. 進入 ARCOSharp 專案目錄
cd D:\AntiGravity\ARCOSharp

# 2. 執行全自動端到端回歸測試 (自動啟動虛擬螢幕視窗 -> 跑測 -> 截圖 -> 自動關閉)
python.exe -X utf8 tools/test_arcosharp_e2e.py

# 3. 選用參數：保持視窗不關閉 (供人工肉眼覆核)
python.exe -X utf8 tools/test_arcosharp_e2e.py --keep-alive

# 4. 選用參數：連接已在運行的 ARCOS.App 實例 (不重開進程)
python.exe -X utf8 tools/test_arcosharp_e2e.py --no-launch
```

---

## 4. 七大測試階段詳細驗證標準 (Phase Specifications)

完整的操作流涵蓋自動化機台的核心工作情境：

### [Phase 1] 權限與操作模式切換 (Operator / Supervisor)
- **測試目標**：驗證產線防呆安全與工程師權限。
- **檢驗點**：
  1. 切換至 `Operator` 模式時，`isLocked == True`，主 Sequencer 表格進入唯讀鎖定狀態，防止作業員誤觸改動程式。
  2. 切換至 `Supervisor` 模式時，`isLocked == False`，表格即時解鎖，恢復指令修改與導引開啟功能。

### [Phase 2] 工業排程指令組裝 (Sequencer Script Setup)
- **測試目標**：建構 16 行標準工業工藝排程，涵蓋全模組整合。
- **代表性排程清單**：
  ```text
  Line 0:  System@Log Message::==== INITIATING ARCOSHARP INDUSTRIAL FLOW ====
  Line 1:  IO@AddIODriver::DriverName=MockIO,DeviceName=MainIO
  Line 2:  Motion@AddMotion::DriverName=MockMotion,DeviceName=AxisX
  Line 3:  System@IncludeModule::Python;
  Line 4:  Variable@Set::CycleCount,0
  Line 5:  IO@SetPort::P0.0,0.02,ForceUpdate
  Line 6:  IO@WaitPortOn::P0.0,2.0,[[Err_Clamp]]Clamp Failure
  Line 7:  Python@Script::batch_id = "BATCH_2026_A1"; part_ok = True;
  Line 8:  Python@GetVariable::batch_id,ARCOSName=CurrentBatch
  Line 9:  Call@mycsvi.csvi&::Cam1,70,190,HistScore
  Line 10: Variable@Inc::CycleCount
  Line 11: System@Log Message::Process successfully finished.
  Line 12: Thread@Stop::
  Line 13: Thread@ErrorHandler::[[Err_Clamp]]
  Line 14: System@Log Error::Clamp cylinder failed to respond in time.
  Line 15: Thread@Stop::True,Clamp Error
  ```

### [Phase 3] 參數導引視窗互動與兩階段過渡 (Visual Guidance)
- **測試目標**：驗證一般模組導引視窗與 Call 模組兩階段導引視窗之彈出、排版與生命週期。
- **檢驗點**：
  1. **Line 5 (IO@SetPort 導引)**：成功彈出 `IO@SetPort__Parameter.vi`，包含點位選單與延遲設定，截圖保存至 `docs/test_screenshots/01_io_guidance.png`。
  2. **Line 9 (Call@mycsvi.csvi& 兩階段導引)**：
     - **第一階段 (`CallGuidanceWindow`)**：正確列出 `mycsvi.csvi`，Runtime 模式預設核取，截圖存至 `02_call_stage1.png`。
     - **第二階段 (`MyCsviGuidanceDialog`)**：點擊「下一步」後，第一階段隱藏並**以 MainWindow 為 Owner 彈出專屬視窗**，顯示 AOI 直方圖門檻與回傳變數，截圖存至 `03_call_stage2.png`。
     - 點擊確認後，第二階段正常銷毀，參數無誤寫回排程行。

### [Phase 4] 全系統分頁 (10 大 TabItem) 導航巡檢
- **測試目標**：確認系統在多工界面切換下的視覺樹穩定性。
- **遍歷分頁**：
  - `Tab 0: User Tab`（產線主畫面與 Sequencer）
  - `Tab 1: Capture`（相機即時取像）
  - `Tab 2: IMAQ Array`（影像快取陣列）
  - `Tab 3: Point&Path`（點位示教與軌跡）
  - `Tab 4: Modules Info`（模組與驅動狀態）
  - `Tab 5: Threads`（13 執行緒監視）
  - `Tab 6: Parameter&Variable`（變數檢視器）
  - `Tab 7: WaveForm`（DAQ 示波器採樣）
  - `Tab 8: Panel CMD`（自訂面板指令）
  - `Tab 9: Remote`（遠端連線監控）
- **檢驗點**：所有分頁遍歷切換反應時間 $<300\text{ms}$，無排版崩潰或 Visual 斷開異常。

### [Phase 5] 排程單步執行與數據鏈路驗證 (Execution Pipeline)
- **測試目標**：單步調度驗證全模組代碼執行、硬體裝載與變數寫入。
- **檢驗點**：
  1. `IO@AddIODriver` 與 `Motion@AddMotion`：動態成功將驅動實例掛載至 `DriverManager`。
  2. `System@IncludeModule::Python;`：調用 `Init.csvi`，動態繫結本機 `python312.dll`。
  3. `IO@SetPort P0.0`：點位 `P0.0` 狀態即時變為 `True`。
  4. `IO@WaitPortOn P0.0`：偵測到狀態為 `True` 立即放行（未觸發自癒標籤）。
  5. `Python@Script` & `Python@GetVariable`：Python 運算批號並成功寫入 ARCOS 變數 `$CurrentBatch = 'BATCH_2026_A1'`。
  6. `Call@mycsvi.csvi&`：執行 AOI 直方圖分析，依據公式 $\frac{70 + 190}{2} \times 0.85 = 110.5$ 成功輸出並寫入 `$HistScore`。
  7. `Variable@Inc CycleCount`：變數數值由 `0` 遞增為 `1`。
  8. `Thread@Stop`：排程安全退出，主狀態轉為 IDLE。

### [Phase 6] 即時硬體點位反轉交互 (Live I/O Interlock)
- **測試目標**：驗證外部遠端控制項與底層 `IoEngine` 之即時雙向通訊。
- **檢驗點**：
  1. 呼叫 `arcos_io_action: toggle` 反轉 `P0.3` $\rightarrow$ 狀態變為 `True`。
  2. 再次反轉 `P0.3` $\rightarrow$ 狀態精準變回 `False`。

### [Phase 7] 系統生產稽核日誌檢閱 (System Audit Logs)
- **測試目標**：驗證工控事件審計的時序與完整性。
- **檢驗點**：`TxtUserLogs` 必須依序包含：
  - `[IncludeModule] Module 'Python' initialized successfully via Init.csvi.`
  - `[Python][Session:Global] GetVariable 'batch_id' -> $CurrentBatch = 'BATCH_2026_A1'`
  - `[Call Module] Executed CSVI 'mycsvi.csvi' successfully. Result: 110.5`
  - `Process successfully finished.`

---

## 5. 常見問題與故障排查 (Troubleshooting)

### Q1: 執行時回報「啟動失敗」或「找不到虛擬顯示器」
- **排查**：請執行 `python.exe -X utf8 "...\cockpit_cli.py" display status`，確認 `DISPLAY9` 處於啟用狀態。若無，可執行 `display add` 重新掛載虛擬螢幕。

### Q2: TCP 19528 連線超時
- **排查**：請檢查是否有殘留的 `ARCOS.App.exe` 佔用了 19528 埠號。可透過 PowerShell 執行 `Get-Process ARCOS.App | Stop-Process -Force` 清理後重新執行。

### Q3: Python 模組執行回報 DLL 載入失敗
- **排查**：請確認本機 `C:\Python312\python312.dll` 是否存在。若安裝於其他路徑，請確保該路徑已加入系統環境變數 `PATH`。

### Q4: 導引視窗開啟回報 `dialogOpened: false`
- **排查**：`SequencerEditorControl` 位於 `User Tab` (Index 0)。若當前畫面處於其他分頁，視窗視覺樹可能處於離線狀態。請確保先切換至 `tabIndex: 0` 再開啟導引視窗。

---

## 6. AI Agent 維護規範

未來任何 AI Agent 若涉及修改以下模組：
- `src/ARCOS.Core/Execution/Handlers/` (直譯執行器)
- `src/ARCOS.App/Controls/SequencerEditorControl.axaml.cs` (排程編輯器)
- `src/ARCOS.App/Dialogs/` (各類導引對話盒)
- `src/MODULES/PYTHON/` 或 `src/EXTERNALMODULES/`

**在提交任務回報前，必須強制執行本端到端測試並確認通過：**
```powershell
python.exe -X utf8 tools/test_arcosharp_e2e.py
```
**唯有 7 大階段全數輸出 `[PASS]`，方可視為驗收完成！**


> [!TIP]
> **自主 Subagent 測試協同**：若欲節省 Token 並執行人機零干擾 UI 巡檢，可參考全域技能 [cockpit-subagent-testing](file:///D:/Users/arco0/.gemini/config/skills/cockpit-subagent-testing/SKILL.md)，指派輕量級 `flash_lite` Subagent 依循標準「遇錯即停」紀律執行全自動操作與缺陷排查。
