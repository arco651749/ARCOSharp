# ARCOSharp Basic Engines 開發手冊與 API 規格指南

## 一、概述與核心架構

ARCOSharp 採用 **Non-reentrant Core Data（非重入純資料核心）** 架構，將工控自動化運行所需的所有狀態、點位、變數、影像、波形與計時機制，解耦並封裝為 **6 大基礎引擎 (Basic Engines)**。

這 6 大引擎統整於依賴注入容器類別 [`BasicEngines`](file:///D:/AntiGravity/ARCOSharp/src/ARCOS.Core/Drivers/BasicEngines.cs) 中：

```csharp
namespace ARCOS.Core.Drivers;

public class BasicEngines
{
    public IoEngine Io { get; }
    public VariableEngine Variable { get; }
    public PointEngine Point { get; }
    public ImageEngine Image { get; }
    public ImageEngine Imaq => Image;  // 相容別名
    public DaqEngine Daq { get; }
    public TimerEngine Timer { get; }
}
```

### 1. 引擎存取位置 (Injection Points)
所有外部模組、驅動與自訂 UI 前面板均透過注入的 `BasicEngines` 實例操作底層核心：
1. **硬體驅動實作 (`IDriver`)**：於 `driver.Init(string param, BasicEngines engines)` 注入。
2. **指令處理常式 (`IModuleHandler`)**：於 `handler.Execute(action, param, ctx)` 中的 `ctx.Engines` 存取。
3. **參數導引視窗 (`IParameterGuidance`)**：於 `ShowGuidanceDialogAsync(param, engines, owner)` 動態感知硬體資源。
4. **自訂 HMI 前面板 (`UserPanel` / `FrontPanel`)**：於 `ctrl.Init(engines)` 或 `ctrl.RefreshFromEngines(engines)` 綁定。
5. **主畫面與 Sequencer (`MainWindow`)**：常駐單例於 `mainWindow.Engines`。

---

## 二、6 大 Basic Engine 詳細規格與 API

### 1. `IoEngine` — 數位 IO 與狀態旗標池
- **主要職責**：管理 512+ 實體點位（`P0.0` ~ `P63.7`）、語意別名（Alias）、虛擬點位（`Start?`、`System#Pause?`、`$(Motor)#Done?`）與硬體驅動掛載。
- **執行緒安全**：內部使用讀寫鎖與並行字典，支援多執行緒並發讀寫。

#### 核心 API 簽章
```csharp
// 1. 點位讀取與寫入 (支援實體編號如 P0.0 或別名如 "SafetyDoor")
bool GetPort(string nameOrPort);
void SetPort(string nameOrPort, bool value);

// 2. 批次讀寫 (連續 bit 陣列)
bool[] GetBatch(string startPort, int bitCount);
void SetBatch(string startPort, ReadOnlySpan<bool> values);

// 3. 命名與別名映射 (將實體 P0.0 綁定至語意名稱)
void SetPortName(string physicalPort, string aliasName);

// 4. 動態新增軟體虛擬 IO (以 '?' 結尾的旗標)
void AddVirtualIO(string name, bool initialValue);

// 5. 移位暫存器 (流水線移位)
void Shift(IReadOnlyList<string> ioNames, int shiftCount);

// 6. 點位枚舉與下拉感知 (供導引視窗與 UI 使用)
IReadOnlyList<string> GetConfiguredPortNames(IoPortDirection direction = IoPortDirection.All);
IReadOnlyList<string> GetPhysicalPortList(int maxPort = 64);
IReadOnlyList<string> GetAllPortNames();

// 7. 狀態變更廣播事件
event Action<string, string, bool>? IoChanged; // (port, alias, newState)
```

#### 使用範例
```csharp
// 讀取進料感應器 (支援別名)
bool hasPart = engines.Io.GetPort("PartInFeed");

// 輸出點火汽缸 (實體點)
engines.Io.SetPort("P0.5", true);

// 設置軟體暫存旗標
engines.Io.AddVirtualIO("InspectReady?", true);
```

---

### 2. `VariableEngine` — 全域與多執行緒分區變數
- **主要職責**：動態字串/數值儲存、參數巨集代換（`$var`）、數學表達式計算、列表/佇列（LPush/RPop）、集合操作（SAdd/SUnion）、執行緒間 Pub/Sub 通訊與 INI 檔案持久化。
- **分區機制**：預設儲存於 `Global` 區塊，多執行緒環境下可獨立分區（如 `Thread0` ~ `Thread12`）。

#### 核心 API 簽章
```csharp
// 1. 基本變數存取
string Get(string keyExpr, string currentSection = "Global");
void Set(string key, string value, string targetSection = "Global");
bool Delete(string key, string targetSection = "Global");

// 2. 參數過濾與表達式求值 (將 "$Total + 1" 或 "$Speed * 2.5" 計算為具體字串)
string FilterParameters(string rawParam, string currentSection = "Global");

// 3. 數值運算與字串處理
void Inc(string key, double step = 1, string section = "Global");
void Dec(string key, double step = 1, string section = "Global");
void Append(string key, string suffix, string section = "Global");
void Trim(string key, int start, int length, string section = "Global");
void Slice(string key, string separator, string section = "Global");
void Joint(string key, string separator, string section = "Global");

// 4. 列表與佇列操作 (雙端隊列)
void LPush(string key, string val, string section = "Global");
void RPush(string key, string val, string section = "Global");
string LPop(string key, string section = "Global");
string RPop(string key, string section = "Global");
void LResetIndex(string key, int index = 0, string section = "Global");
bool LMoveNext(string key, string section = "Global");
bool LFindIndexSet(string keyArr, string targetVal, IEnumerable<string> linkedArrs, string section = "Global");

// 5. 跨執行緒發布與訂閱 (IPC Pub/Sub)
void Publish(string varName);
bool Subscribe(string varName, int timeoutMs = 10000);
void Flush(string varName);

// 6. 持久化存檔與載入
void SaveToFile(string filePath, IEnumerable<string>? keys = null);
void LoadFromFile(string filePath);
```

#### 使用範例
```csharp
// 設定生產目標
engines.Variable.Set("$TargetYield", "99.5");

// 產量遞增 1
engines.Variable.Inc("$TotalParts", 1);

// 參數表達式解析
string evaluated = engines.Variable.FilterParameters("Speed=$BaseSpeed * 1.5, Axis=X");
```

---

### 3. `PointEngine` — 三維點位與 Tray 盤陣列管理器
- **主要職責**：管理多軸機台點位（`MachinePoint`: X, Y, Z, U, V, W）、點位陣列、Tray 盤（Pallet 1D/2D）自動推進游標、圓弧插補路徑生成與工件座標系（Work Space, WS）旋轉平移變換。
- **旗標聯鎖**：自動將托盤是否抵達最後一點的狀態回寫至 `IoEngine` 的 `$(PalletName)#Last?`。

#### 核心 API 簽章
```csharp
// 1. 單點存取 (支援 "P_PICK" 或數值表達式 "(100.0, 200.0, -50.0)")
MachinePoint GetPoint(string pointExpr);
void SetPoint(string name, MachinePoint point);

// 2. 點位陣列存取
IReadOnlyList<MachinePoint> GetPointArray(string name);
void AppendPoint(string name, MachinePoint point);

// 3. 托盤 (Pallet / Tray) 游標推進與自動檢查
MachinePoint GetAndMoveNextPallet(string name);
void ResetPalletIndex(string name, int index = 0);
bool CheckPalletLast(string name);

// 4. 自動路徑與托盤陣列生成
void GeneratePallet1D(string name, int totalCount);
void GeneratePallet2D(string name, int countX, int countY);
void GenerateCircle2DPath(string name, double startDeg, double endDeg, int segments);

// 5. 工件座標系變換 (Work Space Transformation)
void Point2WS(string pointName, string wsName); // 機械座標 -> 工件座標
void WS2Point(string pointName, string wsName); // 工件座標 -> 機械座標
bool CheckInRangeXY(string pointName, string rangePtName);

// 6. 點位檔案持久化 (.csv / .pt)
void SaveToFile(string? filePath = null);
void LoadFromFile(string? filePath = null);
```

#### 使用範例
```csharp
// 取得取料點座標
MachinePoint pickPt = engines.Point.GetPoint("P_PICK");

// 取出下一個 Tray 盤點位並自動推進游標
MachinePoint trayPt = engines.Point.GetAndMoveNextPallet("TRAY_01");
if (engines.Io.GetPort("TRAY_01#Last?"))
{
    // 托盤已取空，觸發換盤
}
```

---

### 4. `ImageEngine` (別名 `Imaq`) — 視覺緩衝與多相機取像
- **主要職責**：多相機影像管理（OpenCvSharp `Mat`）、影像幀陣列、多相機動態取像、ROI 幾何標註設定、視覺輔助圖元（Overlay: 十字線、圓、框、文字）繪製與相機校正矩陣。

#### 核心 API 簽章
```csharp
// 1. 單張即時畫面設定與獲取
void SetSingleImage(string name, Mat image);
ImaqFrame GetFrame(string nameExpr);

// 2. 影像佇列與歷史幀管理
void AppendImage(string arrayName, Mat image);
IReadOnlyList<ImaqFrame> GetFrameArray(string name);
void ClearImaq(string name);
void SetIndex(string name, int index);
bool MoveNext(string name);

// 3. 視覺幾何圖元 (Overlays)
void AddOverlay(string name, OverlayShape shape);
void ClearOverlays(string name);

// 4. 感興趣區域 (ROI)
void SetRoi(string name, string roiString); // 格式: "Rect(x,y,w,h)" 或 "Circle(cx,cy,r)"
string GetRoi(string name);

// 5. 影像持久化
void SaveImage(string name, string filePath, bool includeOverlays = false);
void LoadImage(string name, string filePath);
```

#### 使用範例
```csharp
// 驅動寫入最新擷取畫面
engines.Image.SetSingleImage("CAM0", capturedMat);

// 視覺檢測前獲取畫面
var frame = engines.Image.GetFrame("CAM0");
Mat currentMat = frame.Image;

// 疊加檢測結果綠色十字線
engines.Image.AddOverlay("CAM0", new OverlayCross(200, 200, 30, "#10B981"));
```

---

### 5. `DaqEngine` — 高速資料擷取與波形示波器
- **主要職責**：連續類比訊號採樣、多通道波形緩衝、數位濾波（Moving Average、Butterworth LowPass/HighPass）、訊號上下限公差包絡比對（CoverBy）、即時電壓讀值與 CSV 波形持久化。

#### 核心 API 簽章
```csharp
// 1. 波形設定與採樣追加
void SetWaveform(string name, DateTime t0, double dt, double[] yData, int channel = 0);
void AppendSamples(string name, ReadOnlySpan<double> samples, int channel = 0);

// 2. 即時數值與波形物件取得
double GetInstantValue(string name, int channel = 0);
WaveformEntry GetWaveform(string name);

// 3. 數位濾波演算法
void ApplyFilter(string name, DaqFilterType filterType, double param = 0, int channel = 0);

// 4. 品管容許度比對 (Tolerance / Envelope Check)
bool CompareMaxMinInRange(string name, double max, double min, int channel = 0);
bool CompareCoverBy(string name, string upperSpecName, string lowerSpecName, int channel = 0);

// 5. 波形檔案儲存與載入
void SaveWaveform(string name, string? filePath = null, int channel = 0);
void LoadWaveform(string name, string? filePath = null, int channel = 0);
```

#### 使用範例
```csharp
// 寫入採樣波形 (取樣間隔 0.001s = 1kHz)
engines.Daq.SetWaveform("ForceSensor", DateTime.Now, 0.001, rawPressures);

// 檢查最大力量是否在 10N ~ 25N 合格範圍內
bool isOk = engines.Daq.CompareMaxMinInRange("ForceSensor", max: 25.0, min: 10.0);

// 儲存波形至 WAVEFORM 目錄供品檢追溯
engines.Daq.SaveWaveform("ForceSensor", "WAVEFORM/Batch_20261103.csv");
```

---

### 6. `TimerEngine` — 高精度計時器、計數器與階段性能分析
- **主要職責**：微秒級計時、全機暫停狀態同步、特權計時器（`&` 後綴，在全機暫停時持續計時）、循環計數器、動作耗時標籤追蹤（`Record`）與 IO 旗標自動連動（`$(Timer)#Timeout?`、`$(Counter)#Over?`）。

#### 核心 API 簽章
```csharp
// 1. 計時器操作 (支援特權計時器，如 "&HeaterTimer")
void TimerReset(string timerName, double timeoutSeconds);
bool TimerCheck(string timerName); // 若超時回傳 true，並將 IoEngine 的 $(timerName)#Timeout? 設為 true
long GetElapsedMs(string timerName);

// 2. 計數器操作
void CounterReset(string counterName, int targetCount);
bool CounterCheck(string counterName); // 若達標回傳 true，並將 IoEngine 的 $(counterName)#Over? 設為 true
int GetCount(string counterName);

// 3. 階段效能標籤追蹤 (Profiling / Cycle Time Record)
void Record(string recordName, string message);
double GetRecordElapsedSeconds(string recordName);

// 4. 全機暫停狀態廣播 (同步暫停非特權計時器)
void SetSystemPause(bool isPaused);
```

#### 使用範例
```csharp
// 啟動 3.5 秒超時檢查
engines.Timer.TimerReset("WaitCylinderFwd", 3.5);

// 在輪詢迴圈中檢查是否超時
if (engines.Timer.TimerCheck("WaitCylinderFwd"))
{
    // 汽缸未於 3.5s 到位，進入 Alarm
}

// 啟動不受機台暫停影響的特權計時器 (例如膠水固化時間)
engines.Timer.TimerReset("&GlueCuring", 60.0);
```

---

## 三、架構總覽對照表

| 引擎名稱 | 容器屬性 | 主要資料結構 | 自動連動之虛擬 IO 旗標 | 典型應用場景 |
| :--- | :--- | :--- | :--- | :--- |
| **`IoEngine`** | `engines.Io` | `IoPortInfo` (512+ 點位、別名、Direction) | `$(Device)#Done?`, `System#Pause?` | 感應器讀取、汽缸閥體開關、警報燈 |
| **`VariableEngine`** | `engines.Variable` | 分區字典 (`Section` ➔ Key-Value) | 無 (透過 `$var` 語法代入) | 配方參數、生產計數、執行緒間訊號交換 |
| **`PointEngine`** | `engines.Point` | `MachinePoint` (X,Y,Z,U,V,W) 陣列 | `$(Pallet)#Last?` | 伺服運動點位、Tray 盤取放料陣列 |
| **`ImageEngine`** | `engines.Image` / `Imaq` | OpenCvSharp `Mat`, `ImaqFrame` 佇列 | `$(Cam)#Snap?` | 相機取像、視覺比對、標記十字中心 |
| **`DaqEngine`** | `engines.Daq` | `WaveformEntry` (時序雙精度浮點陣列) | `$(Daq)#Trigger?` | 示波器圖表、壓力曲線、公差包絡比對 |
| **`TimerEngine`** | `engines.Timer` | `HighResolutionTimer`, `CounterItem` | `$(Timer)#Timeout?`, `$(Counter)#Over?` | 動作超時防護、工件計數、週期耗時量測 |
