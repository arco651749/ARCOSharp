# ARCOSharp ExternalModules 外部獨立擴充模組 (CSVI) 開發手冊與規範

本目錄（`ExternalModules/`）用於放置專案客製化之獨立 **.csvi (C# Virtual Instrument)** 模組。
透過 `Call@` 指令，ARCOSharp 可以在不污染主核心架構的情況下，動態調度並並行執行專案專屬演算法（如 AOI 直方圖分析、影像特徵擷取、特定數學模型、客製通訊協議等）。

---

## 1. 核心架構與設計理念

### 1.1 從 LabVIEW .vi 走向現代化 .csvi
在傳統 ARCOS 中，`Call@` 指令主要用於調用外部 LabVIEW `*.vi`。在現代化的 **ARCOSharp** 中：
- **全面升級為 .csvi 虛擬儀器**：以 .NET 8 / C# / Avalonia 為核心，封裝為單一 `.csvi` (實質為 .NET 類別庫組件)。
- **熱抽換零鎖定 (Hot-Swappable Zero-Locking)**：ARCOSharp 直譯引擎使用 `File.ReadAllBytes` 載入組件，運行中替換、重編譯或更新 `.csvi` 檔案**完全不鎖檔**，可隨時熱覆蓋。
- **向下相容性**：若舊專案腳本仍寫 `Call@MyRoutine.vi`，系統會自動重定向至 `ExternalModules/MyRoutine.csvi`。

### 1.2 兩階段導引 (Two-Stage Guidance)
在 ARCOSharp 流程編輯器 (Sequencer Editor) 中，使用者設定 `Call@` 指令享有兩階段視覺化引導：
1. **第一階段 (Call 統一入口視窗 - CallGuidanceWindow)**：
   - 自動掃描並列出 `ExternalModules/` 下所有可用的 `*.csvi`。
   - 選擇執行模式：**Runtime (&, 預設)**、**Debug (除錯)**、**Reentrant (&&, 多執行緒並行)**。
2. **第二階段 (目標 CSVI 專屬導引視窗)**：
   - 點擊「下一步：配置參數」後，ARCOSharp 動態反射呼叫該 CSVI 內建的 `IParameterGuidance.ShowGuidanceDialogAsync`。
   - 跳出該 CSVI 專門設計的 GUI 介面（如相機選單、直方圖閥值滑桿、計算模式切換），直覺完成參數配置。

---

## 2. 指令語法與執行模式

### 2.1 語法結構
```text
Call@<CsviName>.csvi<Mode>::<Param1>, <Param2>, ..., <ReturnVar>, [[<ErrorLabel>]]<ErrorMsg>
```

| 元素 | 說明 | 範例 |
| :--- | :--- | :--- |
| `Call@` | 模組識別前綴 | `Call@` |
| `<CsviName>.csvi` | 目標虛擬儀器檔名（副檔名 `.csvi` 可省略） | `mycsvi.csvi` 或 `mycsvi` |
| `<Mode>` | 執行模式後綴：`&` (Runtime)、`&&` (Reentrant)、無後綴 (Debug) | `&` |
| `::` | 指令本體與參數之分隔符號 | `::` |
| `<Params>` | 傳遞給 CSVI 的輸入參數，逗號分隔 | `Cam1, 50, 200` |
| `<ReturnVar>` | 接收回傳值的變數名稱（支援 `$Var` 或純識別字） | `HistResult` 或 `$Result` |
| `[[<ErrorLabel>]]` | (選用) 發生例外或失敗時跳轉的標籤 | `[[ErrHandler]]` |
| `<ErrorMsg>` | (選用) 錯誤時拋出的自訂日誌訊息 | `AOI Failed` |

### 2.2 三種執行模式對照表

| 模式 | 語法後綴 | 執行特性 | 適用情境 |
| :--- | :--- | :--- | :--- |
| **Debug** | 無 (例如 `Call@mycsvi.csvi`) | 若 CSVI 具備前端面板，會開啟互動視窗供工程師即時監控調試。 | 演算法研發、閥值微調與單步除錯。 |
| **Runtime** | `&` (例如 `Call@mycsvi.csvi&`) | **(預設)** 背景全速執行，不彈出無關視窗，日誌輸出至主系統。 | 生產線正式量產自動化流程。 |
| **Reentrant** | `&&` (例如 `Call@mycsvi.csvi&&`) | **可重入/多執行緒並行**。每次呼叫皆建立獨立無共享狀態實例。 | 跨 13 虛擬機 Thread 或 SubThread 並行運算。 |

---

## 3. mycsvi.csvi 設計範例拆解 (Reference Implementation)

`ExternalModules/mycsvi.csvi` 是官方標準示範模組，原始碼位於 `src/EXTERNALMODULES/MyCsvi/`。

### 3.1 4-in-1 模組架構
標準 CSVI 採用 4-in-1 規格設計：
1. **中繼資訊清單 (`CSVI.Manifest.json`)**：定義版本、作者、描述與規格。
2. **核心執行邏輯 (`ICallCommand`)**：實作純業務/演算法邏輯。
3. **專屬引導視窗 (`IParameterGuidance` + `Avalonia Window`)**：提供專屬 GUI 設定對話盒。
4. **選用前面板 (`GetFrontPanelControl`)**：供 Debug 模式或嵌入儀表板使用。

### 3.2 執行邏輯 (`MyCsviCommand.cs`)
```csharp
using System;
using System.Collections.Generic;
using ARCOS.Core.Execution;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ExternalModules.MyCsvi;

public class MyCsviCommand : ICallCommand
{
    public object? Execute(IReadOnlyList<string> args, ExecutionContext ctx)
    {
        // 模式 A：純數值運算 (例如輸入 10, 20, $Result)
        if (args.Count >= 2 && double.TryParse(args[0], out double v1) && double.TryParse(args[1], out double v2))
        {
            double sum = v1 + v2;
            ctx.Log($"[mycsvi.csvi] Calculation mode: {v1} + {v2} = {sum}");
            return sum;
        }

        // 模式 B：AOI 直方圖運算 (例如輸入 Cam1, 0, 255, HistResult)
        string camName = args.Count > 0 ? args[0].Trim() : "Cam1";
        int minThresh = args.Count > 1 && int.TryParse(args[1], out int min) ? min : 0;
        int maxThresh = args.Count > 2 && int.TryParse(args[2], out int max) ? max : 255;

        ctx.Log($"[mycsvi.csvi] AOI Histogram: {camName} [{minThresh}, {maxThresh}]");
        double score = Math.Round((minThresh + maxThresh) / 2.0 * 0.85, 2);
        return score;
    }
}
```

### 3.3 專屬參數導引 (`MyCsviGuidance.cs` & `MyCsviGuidanceDialog.axaml`)
- 使用者可在 UI 上切換「數值相加模式」或「AOI 直方圖模式」。
- 包含滑桿調節 `MinThreshold` (0~255) 與 `MaxThreshold` (0~255)。
- 即時預覽最終產生的參數字串（如 `Cam1, 40, 220, HistResult`）。

---

## 4. 腳本調用範例

### 範例 1：數值運算並儲存結果
```text
// 調用 mycsvi.csvi 計算 15.5 + 24.5，並將 40 寫入變數 CalcSum
Call@mycsvi.csvi&::15.5, 24.5, CalcSum;
Variable@Log::CalcSum,計算結果:;
```

### 範例 2：AOI 直方圖分析並防呆跳轉
```text
// 調用 mycsvi.csvi 進行視覺直方圖分析，若失敗跳轉至 [[Err_Vision]]
Call@mycsvi.csvi&::Cam1, 30, 200, HistScore, [[Err_Vision]]AOI運算逾時或異常;
Variable@Log::HistScore,直方圖特徵得分:;

Thread@Stop::;

// 錯誤處理標籤
[[Err_Vision]]
Variable@Set::HasError,True;
Thread@Stop::True,視覺直方圖檢查失敗;
```

### 範例 3：多執行緒並行調用 (Reentrant && 模式)
```text
// Thread 1 與 Thread 2 同步呼叫 mycsvi.csvi&&，無狀態競爭
Call@mycsvi.csvi&&::10, 20, Thread1Result;
Call@mycsvi.csvi&&::Cam2, 50, 180, Thread2Result;
```

---

## 5. AI Agent 開發指南與品質檢查清單 (AI Agent Rules)

當 AI Agent 收到使用者指示開發新的 `ExternalModules/*.csvi` 模組時，必須嚴格遵守以下規範：

### 5.1 專案結構標準
新模組原始碼請置於 `src/EXTERNALMODULES/<ModuleName>/`：
```text
src/EXTERNALMODULES/<ModuleName>/
  ├── <ModuleName>.csproj
  ├── CSVI.Manifest.json
  ├── <ModuleName>Command.cs       // 實作 ICallCommand
  ├── <ModuleName>Guidance.cs      // 實作 IParameterGuidance
  └── <ModuleName>GuidanceDialog.axaml / .axaml.cs // 專屬 UI (選用但強烈推薦)
```

### 5.2 核心介面規範
1. **實作 `ICallCommand`**：
   - 介面簽章：`object? Execute(IReadOnlyList<string> args, ExecutionContext ctx)`
   - 回傳值可為 `double`, `int`, `string`, `bool` 等，系統會自動轉換並注入變數引擎。
2. **無狀態可重入保證 (Reentrant Guarantee)**：
   - 嚴禁在 `ICallCommand` 類別內使用靜態可變欄位 (`static mutable fields`)。
   - 所有資料運算應限定於區域變數或依賴傳入的 `ExecutionContext` 與參數，確保在 `&&` 模式下跨多執行緒並行安全。
3. **無鎖定編譯部署**：
   - 編譯建置：`dotnet build -c Release`
   - 將產出的 DLL 複製命名為 `ExternalModules/<ModuleName>.csvi` 以及 `dist/ARCOS_Runtime/ExternalModules/<ModuleName>.csvi`。

### 5.3 測試驗證要求
- 任何新增的 CSVI 模組必須在 `tests/ARCOS.Core.Tests/` 補充單元測試，驗證：
  1. 參數解析與回傳值寫入變數正確性。
  2. 錯誤標籤 (`[[ErrorLabel]]`) 跳轉機制。
  3. 並行呼叫無死鎖與資料污染。
