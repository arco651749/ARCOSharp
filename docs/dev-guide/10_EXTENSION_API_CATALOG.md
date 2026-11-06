# 10. 擴充者可用 API 總目錄 (Extension API Catalog)

本目錄專門為二次開發者整理：在撰寫**自訂模組 (Module Handler)**、**硬體驅動 (Driver)**、**參數導引 (Guidance)**、**單檔外掛 (External CSVI)** 或 **自訂操作面板 (User Panel)** 時，**ARCOSharp 究竟提供了哪些 Public API 供開發者直接呼叫與存取**。

---

## 1. 擴充點 × 可用 API 類別速查矩陣 (Capability Matrix)

| API 服務類別 | 模組處理器<br/>`IModuleHandler` | 硬體驅動<br/>`IDriver` | 參數導引<br/>`IParameterGuidance` | 單檔外掛<br/>`ICallCommand` | 自訂面板<br/>`UserPanel.csvi` | 外部自動化<br/>`MCP / Remoter` |
|---|:---:|:---:|:---:|:---:|:---:|:---:|
| **數位/類比 IO (`IoEngine`)** | ✅ 完整 | ✅ 完整 | ✅ 讀取/別名 | ✅ 完整 | ✅ 完整 | ✅ 完整 |
| **變數計算 (`VariableEngine`)** | ✅ 完整 | ❌ | ❌ | ✅ 完整 | ✅ 完整 | ✅ 完整 |
| **空間點位 (`PointEngine`)** | ✅ 完整 | ❌ | ❌ | ✅ 完整 | ✅ 完整 | ✅ 完整 |
| **視覺影像 (`ImageEngine`)** | ✅ 完整 | ✅ 寫入影格 | ❌ | ✅ 完整 | ✅ 讀取渲染 | ✅ 完整 |
| **採樣波形 (`DaqEngine`)** | ✅ 完整 | ✅ 寫入波形 | ❌ | ✅ 完整 | ✅ 讀取繪圖 | ❌ |
| **碼錶計數 (`TimerEngine`)** | ✅ 完整 | ❌ | ❌ | ✅ 完整 | ✅ 完整 | ❌ |
| **微步狀態機 (`CommandState`)** | ✅ 核心依賴 | ❌ | ❌ | ❌ | ❌ | ❌ |
| **流程調度 (`CommandResult`)** | ✅ 核心依賴 | ❌ | ❌ | ❌ (直傳object)| ❌ | ❌ |
| **驅動票據 (`DriverTicket`)** | ✅ 派發票據 | ✅ 接收處理 | ❌ | ❌ | ❌ | ❌ |
| **系統日誌與報警 (`ctx.Log`)** | ✅ 內建 | ❌ (透過Ticket)| ❌ | ✅ 內建 | ❌ | ✅ 內建 |
| **路徑解析 (`ArcosPaths`)** | ✅ 完整 | ✅ 完整 | ✅ 完整 | ✅ 完整 | ✅ 完整 | ✅ 完整 |
| **主視窗控制 (`Window owner`)** | ❌ | ❌ | ✅ 模態對齊 | ❌ | ✅ 容器掛載 | ❌ |

---

## 2. 各類別核心 API 詳細規格

### 2.1 數位與類比 IO 控制 (`engines.Io`)
所有實作點位讀寫的擴充功能，皆可透過 `BasicEngines.Io` 進行操作：

| API 簽章 | 用途說明 | 適用擴充點 | 執行緒限制 | 範例與注意事項 |
|---|---|---|---|---|
| `bool GetPort(string portNameOrAlias)` | 讀取單一點位布林值 (ON/OFF)。 | Handler, Driver, Csvi, Panel | Thread-safe (無鎖) | `bool on = engines.Io.GetPort("P0.0");` |
| `void SetPort(string portNameOrAlias, bool value)` | 設定單一點位輸出狀態。 | Handler, Driver, Csvi, Panel | Thread-safe | `engines.Io.SetPort("DO_Valve", true);` |
| `void SetPortName(string portId, string alias)` | 動態將硬體點位綁定自訂別名。 | Handler, Panel | Thread-safe (立即廣播) | `engines.Io.SetPortName("P0.3", "Cylinder_Sensor");` |
| `IReadOnlyDictionary<string, string> GetConfiguredPortNames()` | 取得目前所有已註冊點位別名對照表。 | Guidance, Panel, Handler | Thread-safe | 常用於參數導引視窗填充 ComboBox 下拉選單。 |
| `void AddVirtualIO(string name, bool initialValue)` | 新增軟體記憶體虛擬點位。 | Handler, Panel | Thread-safe | 用於模組間純軟體交握旗標。 |

---

### 2.2 全域與分區變數服務 (`engines.Variable`)

| API 簽章 | 用途說明 | 適用擴充點 | 執行緒限制 | 範例與注意事項 |
|---|---|---|---|---|
| `string Get(string key, string section = "Global")` | 讀取變數值。不存在則傳回 `""`。 | Handler, Csvi, Panel | Thread-safe | `string sn = engines.Variable.Get("Barcode");` |
| `void Set(string key, string value, string section = "Global")` | 寫入或更新變數值。 | Handler, Csvi, Panel | Thread-safe | `engines.Variable.Set("PassCount", "100", "STATS");` |
| `double Calc(string expr, string section = "Global")` | 動態四則運算求值（支援 `[VarName]` 替換）。 | Handler, Csvi | Thread-safe | `double r = engines.Variable.Calc("[Offset] + 1.25");` |
| `void SaveToFile(string path)` / `LoadFromFile(string path)` | 將變數表序列化至硬碟或載入。 | Handler, Panel | 磁碟 I/O | 適用於配方 (Recipe) 參數存取。 |

---

### 2.3 空間座標與教導點位 (`engines.Point`)

| API 簽章 | 用途說明 | 適用擴充點 | 執行緒限制 | 範例與注意事項 |
|---|---|---|---|---|
| `MachinePoint GetPoint(string name)` | 取得機台 6 軸空間座標 (`X,Y,Z,U,V,W`)。 | Handler, Csvi, Panel | Thread-safe | `var pt = engines.Point.GetPoint("PickPos");` |
| `void SetPoint(string name, MachinePoint pt)` | 儲存或更新教導點位座標。 | Handler, Csvi, Panel | Thread-safe | `engines.Point.SetPoint("SafePos", new MachinePoint(0,0,50,0,0,0));` |

---

### 2.4 視覺取像緩衝區 (`engines.Image` / `engines.Imaq`)

| API 簽章 | 用途說明 | 適用擴充點 | 執行緒限制 | 範例與注意事項 |
|---|---|---|---|---|
| `OpenCvSharp.Mat? GetFrame(string camId)` | 取得指定相機（如 `"Cam0"`）之最新影格。 | Handler, Csvi, Panel | 傳回之 Mat 需自行管理生命週期 | 適用於自訂視覺檢測外掛或 OpenCV 演算法。 |
| `void SetFrame(string camId, OpenCvSharp.Mat mat)` | 將相機擷取到的影像矩陣寫入緩衝區。 | Driver, Handler | Thread-safe | 由相機 Driver 在 Snap/Live 收到影格時寫入。 |
| `void SaveImage(string camId, string path)` | 將相機緩衝區影像存為 BMP/JPG/PNG。 | Handler, Csvi | 磁碟 I/O | `engines.Image.SaveImage("Cam0", "D:\\NG.png");` |

---

### 2.5 執行緒上下文與微步控制 (`ExecutionContext ctx`)

模組 Handler 與 External CSVI 在執行時，核心皆會傳入 `ExecutionContext ctx`：

| API 屬性 / 方法 | 用途說明 | 適用擴充點 | 範例與規範 |
|---|---|---|---|
| `ctx.State.Step` | 微步步驟暫存索引（`int`）。 | `IModuleHandler` | 用於非阻塞等待狀態機切換（0: 初始化, 1: 輪詢）。 |
| `ctx.State.TimerMs` | 微步起始時間戳記（毫秒）。 | `IModuleHandler` | 用於判斷硬體動作是否逾時，取代 `Thread.Sleep`。 |
| `ctx.Log(string msg, bool isError)` | 寫入系統日誌並廣播至主畫面與檔案。 | Handler, Csvi | `ctx.Log("Laser beam enabled");` 若 `isError=true` 自動設定 `System#Error?` 旗標。 |
| `ctx.Engines` | 直接存取上述六大子引擎。 | Handler, Csvi | `ctx.Engines.Io.SetPort(...)` |
| `ctx.Drivers` | 存取硬體驅動管理器。 | Handler | `var dev = ctx.Drivers.GetDevice("IO", "StationIO");` |

---

### 2.6 指令回傳工廠 (`CommandResult`)

模組 Handler 必須回傳 `CommandResult` 告知虛擬機器後續行為：

| 工廠方法 | 意義與使用情境 |
|---|---|
| `CommandResult.Done()` | 指令成功完成，VM 將 PC 自動前進 1 行，並歸零 `ctx.State`。 |
| `CommandResult.Wait()` | 動作仍在等待中，VM 保持目前 PC，下個調度循環再次重入 `Execute()`。 |
| `CommandResult.Jump(int line)` / `Jump(string label)` | 流程跳轉至指定行號或標籤。 |
| `CommandResult.Call(int progNo)` | 呼叫副程式，將返回點壓入呼叫堆疊。 |
| `CommandResult.Return()` | 副程式返回，自呼叫堆疊彈出。 |
| `CommandResult.Fail(string errorMsg, string? errorLabel)` | 回報錯誤。機台停機警報，或若有 `errorLabel` 則跳轉至錯誤處理區段。 |
| `CommandResult.Retry()` | 原地重試上一失敗指令。 |
| `CommandResult.Stop(string? msg)` | 終止目前執行緒。 |

---

### 2.7 硬體驅動通訊票據 (`DriverTicket`)

模組與驅動程式溝通的唯一標準媒介：

```csharp
// 發送端 (Handler)：
var ticket = new DriverTicket("GoPoint", "P_PICK");
driver.PostTicket(ticket);
var response = ticket.Wait(3000); // 支援非同步 await ticket.Task 或同步限時等待
if (response.Success) {
    // 成功
}

// 接收端 (Driver)：
public void PostTicket(DriverTicket ticket) {
    _ticketQueue.Writer.TryWrite(ticket);
}

// Driver 背景 WorkerLoop 處理：
private void ExecuteCase(DriverTicket ticket) {
    if (ticket.Command == "GoPoint") {
        // 執行軸卡運動...
        ticket.SetCompleted(); // 或 ticket.SetCompleted(DriverResponse.Ok(payload))
    }
}
```

---

## 3. 穩定 API vs. 不建議依賴之內部實作

為確保未來的版本升級不會破壞您的二次開發代碼，請務必遵循以下界線：

### 🟢 建議依賴之穩定擴充介面 (Public Stable APIs)
1. **`BasicEngines` 及其 6 大子引擎**：官方承諾跨版本向後相容。
2. **`IModuleHandler` 與 `CommandResult`**：模組擴充標準核心契約。
3. **`ICallCommand`**：單檔外掛標準契約。
4. **`IDriver` 與 `DriverTicket`**：硬體驅動標準契約。
5. **`IParameterGuidance`**：參數導引對話框標準契約。
6. **`ArcosPaths` 各項路徑解析方法**：系統拓撲定位首選。

### 🔴 不建議擴充者依賴之內部實作 (Internal / High Coupling)
1. **`MainWindow` 的私有欄位與直接參照**：UI 內部元件隨時可能重構，外掛絕不可嘗試強轉 `owner` 為 `MainWindow` 來修改內部元件。
2. **`SequencerRowViewModel` 的內部屬性**：編輯器內部資料結構僅供排程器自身使用。
3. **`ArcosThreadVM` 的底層調度器**：外掛請透過 `CommandResult` 驅動流程，切勿自行呼叫 `VM.Step()` 或竄改 `VM.MainThreads` 集合。
