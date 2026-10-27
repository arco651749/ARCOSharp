# MVCon Call 模組參考手冊

這是針對 **MVCon Call 模組** 專門設計的 Agent 技能指南。身為 MVCon 自動化系統專家，當遇到需要執行外部程式（特別是 LabVIEW VI）、傳遞參數或接收回傳值時，請參照以下語法規則與指令。

## 🎯 啟動條件與職責
當使用者的請求、任務或討論內容中明確提到「**呼叫程式**」、「**執行 VI**」、「**傳遞參數**」或「**外部程式**」等關鍵字時，請自動套用本技能。

## 🛠️ 基本語法與格式規範
1. **指令句型**：所有的 Call 指令都必須以 `Call@` 開頭，後接外部程式名稱（目前只支援 *.vi），再以 `::` 區隔參數。
2. **參數分隔**：多個參數之間必須使用逗號 `,` 隔開。
3. **檔案位置**：所有的外部 VI 檔案統一放置於 `ExternalModules/` 目錄下。

---

## 📜 常用指令對照表

### 1. 指令語法

| 指令格式 | 說明 |
| :--- | :--- |
| `Call@VIName.vi::` | 呼叫外部 LabVIEW VI，不傳遞任何參數。 |
| `Call@VIName.vi::$param1, $param2, ...` | 呼叫外部 LabVIEW VI，並傳入多個輸入參數。 |
| `Call@VIName.vi::$param1, $param2, $return_var` | 呼叫外部 LabVIEW VI，並將回傳值存入 `$return_var` 變數中。 |
| `Call@VIName.vi::$param1, $param2, $return_var, [[$label]]$Message` | 呼叫外部 LabVIEW VI，並在發生錯誤時跳轉到指定標籤。 |

### 2. 模式

| 指令格式 | 模式 | 行為 | 適用場景 |
| :--- | :--- | :--- | :--- |
| `Call@VIName.vi` | **Debug** | 執行時會開啟程式視窗，可看到執行過程及結果。 | 開發／測試階段，驗證 VI 正確性。 |
| `Call@VIName.vi&` | **Runtime** | 執行時不會開啟程式視窗。 | 正式環境佈署。 |
| `Call@VIName.vi&&` | **Reentrant** | 執行時不會開啟程式視窗，且可重複呼叫同一程式。 | 需要在多執行緒中同時執行同一 VI 的場景。 |

> [!NOTE]
> AI Agent 協助生成 MVCon 程式時，預設請使用 **Runtime** 模式。

> [!IMPORTANT]
> **Reentrant 模式前置條件**：須在 LabVIEW 中將 VI 的 `VI Property -> Execution` 設定為 `Shared Clone Reentrant Execution` 或 `Preallocated Clone Reentrant Execution`。

> [!WARNING]
> 使用 Reentrant 模式時，請注意多執行緒共享資源的競爭問題，避免發生執行緒安全相關錯誤。

---

## 💡 AI 撰寫範例參考

**範例 1：呼叫 LabVIEW VI 並傳遞參數**

若使用者要求呼叫名為 `CheckVision.vi` 的程式，並傳入參數 `Cam1` 和 `target.png`：

```markdown
// 呼叫外部 LabVIEW VI 程式，Debug模式
Call@CheckVision.vi::Cam1,target.png

// 呼叫外部 LabVIEW VI 程式，Runtime模式
Call@CheckVision.vi&::Cam1,target.png

// 呼叫外部 LabVIEW VI 程式，Reentrant模式
Call@CheckVision.vi&&::Cam1,target.png
```

**範例 2：呼叫 LabVIEW VI 並接收回傳值 (預設 Runtime 模式)**

若使用者要求呼叫 `CalculatePosition.vi`，並將回傳結果存入 `ResultPos` 變數：

```markdown
// 呼叫外部 LabVIEW VI 程式並接收回傳值 (Runtime 模式，不開視窗)
Call@CalculatePosition.vi&::Cam1,ResultPos;

// === 結束當前流程 ===
Thread@Stop::
```

**範例 3：帶有錯誤處理的呼叫 (預設 Runtime 模式)**

若使用者要求呼叫 `ProcessImage.vi`，並在發生錯誤時跳轉到 `ErrorProcess` 標籤：

```markdown
// 呼叫外部 LabVIEW VI 程式，並在發生錯誤時跳轉到指定標籤 (Runtime 模式)
Call@ProcessImage.vi&::Cam1,target.png,[[ErrorProcess]]Process Image Failed;

// === 結束當前流程 ===
Thread@Stop::;

// ======== 錯誤處理區段 ======== 
Thread@ErrorHandler::[[ErrorProcess]];
// 設定狀態與彈出訊息
Variable@Set::ErrorState,True;
Thread@Stop::True,Process Image Failed
```
