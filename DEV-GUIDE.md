# ARCOSharp 開發者指南 (Developer Guide & Architectural Invariants)

> **【系統最高架構準則 (Supreme Architectural Invariant)】**  
> **以程式執行速度為第一核心目標，視覺即時更新絕非 ARCOSharp 的重點。**  
> **UI 視覺呈現必須放到合適的時機進行；凡非當前 Active 的 UI 元件、分頁或視窗，絕對嚴禁進行任何更新與重繪！**

---

## 1. 核心哲學：執行速度絕對優先 (Execution Speed is Supreme)

ARCOSharp 的本質是一套專注於工業級自動化機台的高吞吐、低延遲直譯與運動控制系統：
* **核心使命**：多執行緒虛擬機（`ArcosThreadVM`）、單步指令調度器（`CommandExecutor`）、六大子引擎（`BasicEngines`）與硬體驅動票據（`DriverTicket`）的運算效率與極速響應，是整個系統的**唯一最高優先級**。
* **UI 定位為次要觀察者 (Secondary Observer)**：工業機台在連續高速運轉時（例如每秒數千次 IO 判斷、高精度座標解算、微步打卡 Record），**視覺畫面的「即時動態更新」對實際生產毫無效益，反而只會成為效能殺手**。
* **杜絕 UI 拖累後端**：Avalonia UI 的 Layout Measure/Arrange、字串格式化、UI 執行緒切換（`Dispatcher.UIThread`）與垃圾回收（GC）是造成系統卡頓與抖動的主要根源。因此，代碼架構必須保證**即使前端 UI 完全凍結或關閉，後端直譯與硬體控制仍能以全速滿載運轉**。

---

## 2. UI 更新三大鐵律 (Three UI Architectural Invariants)

任何參與 ARCOSharp 開發、模組擴充或維護的工程師與 AI Agent，必須在所有代碼設計與 Code Review 中嚴格遵守以下三大鐵律：

### 🛑 鐵律一：非 Active 的 UI 絕對禁止更新 (Zero-Cost Inactive UI)

凡是**當前不可見、未被選中或非 Active** 的畫面，一律視為「不存在」，嚴禁浪費任何 CPU 週期進行狀態同步：
1. **分頁未選中 (Inactive Tab)**：
   * 若使用者當前停留於 `User Tab`，則背景被遮蔽的 `Capture`、`Modules Info`、`Sequencer Editor` 等面板**嚴禁更新**。
   * 嚴禁在定時器迴圈中遍歷不可見的 512 點 IO 列表、嚴禁刷新隱藏的相機影像、嚴禁更新隱藏的 DRO 座標顯示。
2. **模組未選中 (Inactive Module View)**：
   * 在 `Modules Info` 頁面中，若當前選取的不是 `SYSTEM` 模組，**嚴禁**更新 `RecordIndex`、`TxtSystemCycleTime` 或 2000 格 Record 表格。
   * 若當前選取的不是 `MOTION` 模組，**嚴禁**更新 7 軸同動座標（`TxtDroX` ~ `TxtDroH`）。
3. **視窗不可見或最小化 (Hidden / Minimized / Inactive Window)**：
   * 當主視窗最小化、失去焦點或被其他對話框覆蓋時，所有 UI 輪詢定時器必須立即暫停或直接 Early Exit。
4. **切換至 Active 時才「按需一次性拉取」 (Lazy Pull on Activation)**：
   * UI 應採用「延遲拉取」而非「即時推播」。當使用者主動點擊並切換到特定 Tab 或模組時，由 UI 事件觸發一次性的狀態快照載入，平時在背景保持零開銷。

---

### 🛑 鐵律二：嚴禁在直譯器或驅動熱點路徑中同步分發 UI (No Synchronous UI Dispatching in Hot Paths)

1. **零阻塞執行緒**：
   * 嚴禁在 `CommandExecutor`、`IModuleHandler.Execute`、`Driver.ExecuteCommand` 或高速循環中呼叫 `Dispatcher.UIThread.InvokeAsync` 或進行任何同步等待。
2. **純記憶體緩衝**：
   * 任何底層事件（如 `IoChanged`、`Record.Add`、`Log`）**只准寫入執行緒安全的記憶體緩衝區**（如 Concurrent 結構或 Ring Buffer），完全不碰 UI 執行緒。
3. **推遲到合適時機 (Opportunistic Update)**：
   * UI 端採用低頻節流定時器（如 100ms~200ms）或閒置期批次拉取。若非 Active 狀態，連定時器拉取都直接跳過。

---

### 🛑 鐵律三：環狀固定容量緩衝區，杜絕動態擴容 GC (Pre-allocated Ring Buffer, Zero Allocation)

1. **固定槽位與原地賦值**：
   * 針對高頻記錄（如 `Record`、`SYSTEMLOG`、波形取樣）：
   * 嚴禁使用動態 `List.Add`、`ObservableCollection.Add` 或字串 `+=` 拼接，以避免高頻記憶體複製與 GC 凍結。
   * 強制使用預先分配固定容量（如 2000-slot Ring Buffer），透過 `Index % Capacity` 原地更新槽位物件屬性（搭配局部 `INotifyPropertyChanged`）。

---

## 3. 標準程式碼範例與實作模式 (Best Practice Code Patterns)

### ✅ 模式 1：UI 定時器之 Active 守衛模式 (Active Guard Pattern)

在任何 UI 刷新定時器回呼函式（如 `UiTimer_Tick`）中，必須由外向內進行層層守衛：

```csharp
private void UiTimer_Tick(object? sender, EventArgs e)
{
    // 守衛 1：主視窗不可見或最小化時，直接退出
    if (!this.IsVisible || this.WindowState == WindowState.Minimized) return;

    var mainTab = this.FindControl<TabControl>("MainTabControl");
    int activeTabIdx = mainTab?.SelectedIndex ?? 0;

    // 守衛 2：僅當在 Capture Tab (Index 1) 時，才刷新 IO 點位
    if (activeTabIdx == 1)
    {
        UpdateCaptureIoList();
    }

    // 守衛 3：僅當在 Modules Info Tab (Index 2) 時，依據選中模組進行細項刷新
    if (activeTabIdx == 2)
    {
        string selectedModule = GetCurrentlySelectedModule();
        switch (selectedModule)
        {
            case "SYSTEM":
                UpdateSystemModuleUi(); // 僅刷新 SYSTEM 狀態與 Record
                break;
            case "MOTION":
                UpdateMotionDroUi();    // 僅刷新 DRO 座標
                break;
        }
    }

    // 守衛 4：User Tab (Index 0) 僅刷新必要的機台運轉紅綠狀態指示燈
    if (activeTabIdx == 0)
    {
        UpdateUserTabStatusLeds();
    }
}
```

---

### ✅ 模式 2：Tab 切換時的按需拉取模式 (Lazy Catch-Up on Tab Selection)

不要在背景持續維護不可見畫面的資料，改在使用者切換分頁時進行一次性快照刷新：

```csharp
private void OnMainTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
{
    if (sender is not TabControl tc) return;

    switch (tc.SelectedIndex)
    {
        case 1: // 切換至 Capture：一次性同步當前所有 IO 與點位
            RefreshCaptureTabSnapshot();
            break;
        case 2: // 切換至 Modules Info：一次性同步當前選中模組
            RefreshActiveModuleSnapshot();
            break;
    }
}
```

---

### ✅ 模式 3：高速資料寫入純解耦模式 (Decoupled Hot Data Ingestion)

核心記錄操作必須是 $O(1)$ 純記憶體操作，完全不依賴 Dispatcher：

```csharp
public void Record(string recordName, string message)
{
    var channel = _channels.GetOrAdd(recordName, name => new RecordChannel(name, 2000));
    channel.Add(message); // 純記憶體環狀槽位寫入，0 UI Dispatcher 調用，極速返回
}
```

---

## 4. Code Review 檢核清單 (Pull Request & Development Checklist)

在提交任何 ARCOSharp 程式碼變更前，必須自檢以下項目：

- [ ] **無熱路徑 UI 調用**：`ARCOS.Core` 下所有程式碼（直譯器、Handler、子引擎）完全無 `Avalonia` 命名空間引用，不發起任何 UI Thread 調用。
- [ ] **非 Active 不更新**：所有 UI 更新定時器或事件處理常式，均具備 `IsVisible` 與 `ActiveTab` 守衛檢查。
- [ ] **無高頻動態記憶體配置**：高頻日誌與記錄使用預分配環狀緩衝區，禁止在高頻路徑使用 `List.Add` 或字串累加。
- [ ] **異常降級保護**：即使 UI 發生例外或重繪延遲，背景腳本執行緒與硬體通訊完全不受干擾、繼續全速執行。

---

## 5. 詳細技術白皮書與章節目錄

進一步的架構細節與各子系統規格，請參閱 `docs/dev-guide/` 目錄下各專題文檔：

| 章節 | 文件連結 | 核心內容說明 |
|---|---|---|
| **00** | [二次開發指南導覽](docs/dev-guide/00_README.md) | 全套開發規格書索引與 CSVI 三位一體哲學 |
| **01** | [整體架構總覽](docs/dev-guide/01_ARCHITECTURE_OVERVIEW.md) | 分層架構、生命週期、執行緒模型與並發調度 |
| **02** | [ARCOS.Core API 參考](docs/dev-guide/02_CORE_API_REFERENCE.md) | 核心組件 Public API 與直譯器擴充 |
| **03** | [ARCOS.App API 與 UI 規範](docs/dev-guide/03_APP_API_REFERENCE.md) | Avalonia 主程式、參數導引與 MCP 介面 |
| **04** | [ARCOS.Remoter 通訊協定](docs/dev-guide/04_REMOTER_API_REFERENCE.md) | Telnet 遠端遙控 Sidecar 協定 |
| **05** | [動態模組擴充教學](docs/dev-guide/05_EXTEND_MODULE.md) | `IModuleHandler` 與指令擴充 |
| **06** | [硬體驅動與 Driver CSVI 設計](docs/dev-guide/06_EXTEND_DRIVER_CSVI.md) | `IDriver` 與 `DriverTicket` 票據架構 |
| **07** | [ExternalModules 單檔外掛開發](docs/dev-guide/07_EXTEND_EXTERNAL_CSVI.md) | `ICallCommand` 與單檔演算法外掛 |
| **08** | [User Panel 自訂操作面板](docs/dev-guide/08_EXTEND_USER_PANEL.md) | `UserPanel.csvi` 與 HMI 客製化 |
| **09** | [架構瓶頸分析與審查報告](docs/dev-guide/09_ARCHITECTURE_REVIEW.md) | 架構弱點診斷與重構指引 |
| **10** | [擴充者可用 API 總目錄](docs/dev-guide/10_EXTENSION_API_CATALOG.md) | Public API 與子引擎速查手冊 |
| **11** | [邏輯表達式編輯器指南](docs/dev-guide/11_LOGIC_EDITOR_GUIDE.md) | 複合邏輯與運算子體系 |
| **12** | [統一模組中心與 CSVI Studio 規格](docs/dev-guide/12_UNIFIED_MODULES_INFO_SPEC.md) | 內嵌 IDE、六大分頁與 MCP 自動化 |
