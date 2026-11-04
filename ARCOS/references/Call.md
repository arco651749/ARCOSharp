# ARCOS Call 模組參考手冊

這是針對 **ARCOS Call 模組** 專門設計的 Agent 技能指南。身為 ARCOS / ARCOSharp 自動化系統專家，當遇到需要執行外部客製模組（獨立 C# 虛擬儀器 CSVI，或相容之外部程式）、傳遞參數或接收回傳值時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**呼叫程式**」、「**執行 CSVI**」、「**傳遞參數**」或「**外部模組**」等關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 Call 指令都必須以 `Call@` 開頭，後接外部模組名稱（建議使用 `*.csvi`，系統亦向下相容 `*.vi` 自動對應），再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **檔案位置**：所有的外部 CSVI 檔案統一放置於 `ExternalModules/` 目錄下。
4. **回傳值寫入**：若傳入最後一個參數為變數名稱（如 `$Result` 或 `ResultPos`），執行結果將自動寫入變數引擎。

---

## 📜 常用指令對照表

### 1. 指令語法

| 指令格式 | 說明 |
| :--- | :--- |
| `Call@CsviName.csvi::` | 呼叫外部 CSVI 模組，不傳遞任何參數。 |
| `Call@CsviName.csvi::$param1, $param2, ...` | 呼叫外部 CSVI 模組，並傳入多個輸入參數。 |
| `Call@CsviName.csvi::$param1, $param2, $return_var` | 呼叫外部 CSVI 模組，並將回傳值存入 `$return_var` 變數中。 |
| `Call@CsviName.csvi::$param1, $param2, $return_var, [[$label]]$Message` | 呼叫外部 CSVI 模組，並在發生錯誤時跳轉到指定標籤。 |

### 2. 模式

| 指令格式 | 模式 | 行為 | 適用場景 |
| :--- | :--- | :--- | :--- |
| `Call@CsviName.csvi` | **Debug** | 執行時若 CSVI 具備前面板視窗會開啟，可即時查看運算細節與結果。 | 開發／測試階段，驗證 CSVI 正確性。 |
| `Call@CsviName.csvi&` | **Runtime** | **(預設)** 執行時在背景全速運算，不會彈出干擾視窗。 | 正式生產環境佈署。 |
| `Call@CsviName.csvi&&` | **Reentrant** | 執行時不開視窗，支援**多執行緒可重入**（每次呼叫為獨立無狀態實例）。 | 多執行緒並行 (13 VM Threads 或 SubThread) 同步調用同一 CSVI。 |

> [!NOTE]
> AI Agent 協助生成 ARCOS 程式時，預設請使用 **Runtime (`&`)** 模式。
> 若舊有腳本寫為 `Call@MyRoutine.vi&`，ARCOSharp 會自動對應至 `ExternalModules/MyRoutine.csvi`。

> [!IMPORTANT]
> **Reentrant (&&) 模式規範**：CSVI 必須保證無狀態 (Stateless)，嚴禁在 `ICallCommand` 內使用靜態可變欄位，以確保跨多執行緒並行運算安全。

---

## 💡 AI 撰寫範例參考

**範例 1：呼叫視覺分析 CSVI 並傳遞參數 (預設 Runtime 模式)**

若使用者要求呼叫名為 `mycsvi.csvi` 的視覺直方圖分析，並傳入參數 `Cam1`、閥值 `30`、`200`，結果存入 `HistScore`：

```text
// 呼叫外部 CSVI 程式並接收回傳值 (Runtime 模式，不開視窗)
Call@mycsvi.csvi&::Cam1, 30, 200, HistScore;
Variable@Log::HistScore,直方圖分析得分:;

// === 結束當前流程 ===
Thread@Stop::;
```

**範例 2：數值運算並指定變數回傳**

若使用者要求呼叫 `mycsvi.csvi` 進行兩數相加，並將結果存入 `SumResult`：

```text
// 調用 mycsvi.csvi 計算 12.5 + 37.5
Call@mycsvi.csvi&::12.5, 37.5, SumResult;
Variable@Log::SumResult,相加結果為:;
Thread@Stop::;
```

**範例 3：帶有錯誤處理的呼叫**

若使用者要求呼叫 `mycsvi.csvi`，並在發生錯誤或超時時跳轉到 `ErrorProcess` 標籤：

```text
// 呼叫外部 CSVI 程式，並在發生錯誤時跳轉到指定標籤 (Runtime 模式)
Call@mycsvi.csvi&::Cam1, 50, 255, HistScore, [[ErrorProcess]]視覺分析逾時或異常;

// === 結束當前流程 ===
Thread@Stop::;

// ======== 錯誤處理區段 ======== 
[[ErrorProcess]]
Variable@Set::ErrorState,True;
Thread@Stop::True,AOI Histogram Failed;
```
