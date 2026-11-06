# 02. ARCOS.Core API 參考手冊 (Core API Reference)

本手冊詳述 `ARCOS.Core` 類別庫中**所有對外開放之 Public API 型別、方法簽章、參數定義、呼叫情境與注意事項**。

---

## 📑 公開型別索引表 (Public Types Index)

| 命名空間 (Namespace) | 公開型別 (Public Type) | 核心用途與職責 |
|---|---|---|
| `ARCOS.Core.Drivers` | [`BasicEngines`](#1-basicengines) | 六大子引擎集合容器，核心硬體存取門面 |
| `ARCOS.Core.Engines` | [`IoEngine`](#2-ioengine) | 數位/類比 IO 讀寫、點位別名映射與即時快照 |
| `ARCOS.Core.Engines` | [`VariableEngine`](#3-variableengine) | 全域/分區變數表、字串表達式計算與參數持久化 |
| `ARCOS.Core.Engines` | [`PointEngine`](#4-pointengine) | 6 軸機台空間座標 (`X,Y,Z,U,V,W`) 管理 |
| `ARCOS.Core.Engines` | [`ImageEngine`](#5-imageengine) | 視覺取像緩衝區、OpenCV 影像矩陣儲存 |
| `ARCOS.Core.Engines` | [`DaqEngine`](#6-daqengine) | 類比採樣波形數據通道與特徵統計 |
| `ARCOS.Core.Engines` | [`TimerEngine`](#7-timerengine) | 計時器群組與計數器狀態追蹤 |
| `ARCOS.Core.Execution` | [`ExecutionContext`](#8-executioncontext) | 執行緒即時上下文、PC 指標、呼叫堆疊與日誌 |
| `ARCOS.Core.Execution` | [`CommandResult`](#9-commandresult) | 指令執行結果回傳工廠（狀態機轉換元數據） |
| `ARCOS.Core.Execution` | [`IModuleHandler`](#10-imodulehandler) | 自訂功能模組直譯處理器介面 |
| `ARCOS.Core.Execution` | [`ModuleRegistry`](#11-moduleregistry) | 全域模組指令註冊表與動態探索中心 |
| `ARCOS.Core.Execution` | [`CommandExecutor`](#12-commandexecutor) | 單步指令微步調度與語法直譯器 |
| `ARCOS.Core.Execution` | [`ArcosThreadVM`](#13-arcosthreadvm) | 13 主執行緒 + 動態子執行緒虛擬機器排程器 |
| `ARCOS.Core.Drivers` | [`DriverManager`](#14-drivermanager) | 硬體驅動管理與 Directory-Per-Driver 動態載入 |
| `ARCOS.Core.Drivers` | [`DriverTicket`](#15-driverticket) | 跨模組弱耦合硬體通訊票據資料結構 |
| `ARCOS.Core.Drivers` | [`IDriver`](#16-idriver) | 硬體驅動程式標準抽象介面 |
| `ARCOS.Core.Csvi` | [`CsviPackage`](#17-csvipackage) | CSVI 虛擬儀表套件解讀與組件載入器 |
| `ARCOS.Core.Execution` | [`ICallCommand`](#18-icallcommand) | ExternalModules 單檔外掛執行介面 |
| `ARCOS.Core.Guidance` | [`IParameterGuidance`](#19-iparameterguidance) | 參數導引對話框與執行插件標準介面 |
| `ARCOS.Core.Common` | [`ArcosPaths`](#20-arcospaths) | 系統標準拓撲路徑解析器 |
| `ARCOS.Core.Serialization` | [`ScriptSerializer`](#21-scriptserializer) | `.pro` 專案檔與 `.init` 程式腳本序列化工具 |

---

## 1. BasicEngines

- **命名空間**：`ARCOS.Core.Drivers`
- **原始碼路徑**：[`src/ARCOS.Core/Drivers/BasicEngines.cs`](../../src/ARCOS.Core/Drivers/BasicEngines.cs)
- **用途說明**：集中管理機台運行所需的六大基礎子引擎，在系統開機、Driver 載入與 UserPanel 掛載時統一注入。

### 公開屬性
```csharp
public IoEngine Io { get; }
public VariableEngine Variable { get; }
public PointEngine Point { get; }
public ImageEngine Image { get; }
public ImageEngine Imaq { get; } // Image 的別名 (相容 LabVIEW IMAQ 習慣)
public DaqEngine Daq { get; }
public TimerEngine Timer { get; }
```

### 建構式
```csharp
public BasicEngines();
public BasicEngines(IoEngine io, VariableEngine variable, PointEngine point, ImageEngine image, DaqEngine daq, TimerEngine timer);
```

---

## 2. IoEngine

- **命名空間**：`ARCOS.Core.Engines`
- **原始碼路徑**：[`src/ARCOS.Core/Engines/IoEngine.cs`](../../src/ARCOS.Core/Engines/IoEngine.cs)
- **用途說明**：管理所有數位與類比點位之狀態、別名映射（`PortName`）以及虛擬 IO 暫存。

### 公開方法

#### `GetPort(string portNameOrAlias) -> bool`
- **說明**：取得指定點位或別名之布林值（ON/OFF）。
- **呼叫時機**：條件判斷、腳本 `IO@CheckLogicOn`、UI 狀態刷新。
- **範例**：
  ```csharp
  bool isCylinderReady = engines.Io.GetPort("Cylinder_Sensor");
  ```

#### `SetPort(string portNameOrAlias, bool value) -> void`
- **說明**：設定指定實體點位或虛擬點位之輸出狀態。
- **執行緒安全**：內部使用 `ConcurrentDictionary` 與讀寫鎖，保證多執行緒安全。

#### `SetPortName(string portId, string alias) -> void`
- **說明**：動態將硬體點位（如 `P0.4`）綁定自訂別名（如 `Safety_Door`）。綁定後立即廣播更新。

#### `GetConfiguredPortNames() -> IReadOnlyDictionary<string, string>`
- **說明**：取得當前所有已註冊點位別名之完整對應表（鍵為別名，值為點位 ID）。

#### `AddVirtualIO(string name, bool initialValue = false) -> void`
- **說明**：註冊記憶體內部虛擬 IO 旗標，供跨執行緒或跨模組軟體邏輯交握。

---

## 3. VariableEngine

- **命名空間**：`ARCOS.Core.Engines`
- **原始碼路徑**：[`src/ARCOS.Core/Engines/VariableEngine.cs`](../../src/ARCOS.Core/Engines/VariableEngine.cs)
- **用途說明**：提供全域與分區（Section）變數儲存表，支援四則運算表達式求值與設定檔序列化。

### 公開方法

#### `Set(string key, string value, string section = "Global") -> void`
- **說明**：於指定分區寫入鍵值變數。若變數不存在則自動建立。

#### `Get(string key, string section = "Global") -> string`
- **說明**：取得變數值。若不存在則回傳空字串 `""`。

#### `Calc(string expression, string section = "Global") -> double`
- **說明**：動態計算算式（如 `[VarA] * 2 + 10`），自動替換中括號內的變數名稱。

#### `SaveToFile(string filePath) -> void`
- **說明**：將變數表寫入 `.var` 檔案持久化。

#### `LoadFromFile(string filePath) -> void`
- **說明**：從檔案載入變數覆蓋至記憶體。

---

## 4. PointEngine

- **命名空間**：`ARCOS.Core.Engines`
- **原始碼路徑**：[`src/ARCOS.Core/Engines/PointEngine.cs`](../../src/ARCOS.Core/Engines/PointEngine.cs)
- **用途說明**：管理空間點位座標 `MachinePoint (X, Y, Z, U, V, W)`，支援教導點位存檔。

### 公開方法
```csharp
public MachinePoint GetPoint(string pointName);
public void SetPoint(string pointName, MachinePoint point);
public bool HasPoint(string pointName);
public void SaveToFile(string filePath);
public void LoadFromFile(string filePath);
```

---

## 5. ImageEngine

- **命名空間**：`ARCOS.Core.Engines`
- **原始碼路徑**：[`src/ARCOS.Core/Engines/ImageEngine.cs`](../../src/ARCOS.Core/Engines/ImageEngine.cs)
- **用途說明**：影像緩衝區管理，底層整合 OpenCvSharp `Mat`，支援多相機槽位（Cam0, Cam1...）快照與存檔。

### 公開方法
```csharp
public void SetFrame(string camId, OpenCvSharp.Mat mat);
public OpenCvSharp.Mat? GetFrame(string camId);
public void SaveImage(string camId, string filePath);
public bool LoadImage(string camId, string filePath);
```

---

## 6. DaqEngine

- **命名空間**：`ARCOS.Core.Engines`
- **原始碼路徑**：[`src/ARCOS.Core/Engines/DaqEngine.cs`](../../src/ARCOS.Core/Engines/DaqEngine.cs)
- **用途說明**：高速類比訊號採樣波形管理，儲存時間序列 `(Time_s, Value)` 陣列。

### 公開方法
```csharp
public void SetWaveform(string channelName, IReadOnlyList<WaveformEntry> data, double dt, DateTime t0);
public IReadOnlyList<WaveformEntry> GetWaveform(string channelName);
public (double Min, double Max, double Avg) CalculateStats(string channelName);
```

---

## 7. TimerEngine

- **命名空間**：`ARCOS.Core.Engines`
- **原始碼路徑**：[`src/ARCOS.Core/Engines/TimerEngine.cs`](../../src/ARCOS.Core/Engines/TimerEngine.cs)
- **用途說明**：提供命名碼錶與生產計數器服務。

### 公開方法
```csharp
public void ResetTimer(string timerName);
public double GetTimerElapsedSeconds(string timerName);
public void ResetCounter(string counterName);
public int IncrementCounter(string counterName, int step = 1);
public int GetCounter(string counterName);
```

---

## 8. ExecutionContext

- **命名空間**：`ARCOS.Core.Execution`
- **原始碼路徑**：[`src/ARCOS.Core/Execution/ExecutionContext.cs`](../../src/ARCOS.Core/Execution/ExecutionContext.cs)
- **用途說明**：執行緒運行上下文，封裝當前線程的一切資源，為指令 Handler 的唯一環境介面。

### 公開屬性與事件
```csharp
public int ThreadId { get; set; }
public string ThreadName { get; set; }
public ProgramTable Program { get; set; }
public int ProgIndex { get; set; } // 寫入時自動調用 NotifyProgIndex
public int ReportedProgIndex { get; } // 供 UI 異步節流讀取之原子 PC
public CommandState State { get; } // 微步暫存器 (用於非阻塞等待計時)
public Stack<(ProgramTable Program, int ReturnLine)> CallStack { get; }
public BasicEngines Engines { get; }
public DriverManager Drivers { get; }
public ModuleRegistry? Registry { get; set; }
public ArcosThreadVM? VM { get; set; }
public bool IsRunning { get; set; }

public event Action<int>? OnProgIndexNoticed;
public event Action<string, bool>? OnLog;
```

### 公開方法
```csharp
public void NotifyProgIndex(int pc);
public void Log(string message, bool isError = false);
```

---

## 9. CommandResult

- **命名空間**：`ARCOS.Core.Execution`
- **原始碼路徑**：[`src/ARCOS.Core/Execution/CommandResult.cs`](../../src/ARCOS.Core/Execution/CommandResult.cs)
- **用途說明**：指令執行回傳值工廠類別，決定 VM 下一步調度行為。

### 靜態工廠方法
```csharp
public static CommandResult Done();                      // 動作完成，PC + 1
public static CommandResult Wait();                      // 微步等待，保持 PC，下個迴圈重入
public static CommandResult Jump(int line);              // 跳轉至指定行號
public static CommandResult Jump(string label);          // 跳轉至標籤
public static CommandResult Call(int progNo);            // 呼叫副程式
public static CommandResult Return();                    // 副程式返回
public static CommandResult Fail(string errorMsg, string? errorLabel = null); // 失敗報警
public static CommandResult Retry();                     // 原處重試
public static CommandResult Stop(string? msg = null);    // 終止該執行緒
```

---

## 10. IModuleHandler

- **命名空間**：`ARCOS.Core.Execution`
- **原始碼路徑**：[`src/ARCOS.Core/Execution/IModuleHandler.cs`](../../src/ARCOS.Core/Execution/IModuleHandler.cs)
- **用途說明**：功能模組擴充標準介面。任何自訂模組必須實作此介面。

```csharp
public interface IModuleHandler
{
    string ModuleName { get; }
    IReadOnlyList<string> GetSupportedCommands();
    CommandResult Execute(string action, string filteredParam, ExecutionContext ctx);
}
```

---

## 11. ModuleRegistry

- **命名空間**：`ARCOS.Core.Execution`
- **原始碼路徑**：[`src/ARCOS.Core/Execution/ModuleRegistry.cs`](../../src/ARCOS.Core/Execution/ModuleRegistry.cs)
- **用途說明**：全域模組與指令註冊中心。

```csharp
public static ModuleRegistry CreateDefault(string? modulesRootPath = null);
public void RegisterHandler(IModuleHandler handler);
public bool UnregisterHandler(string moduleName, out IModuleHandler? oldHandler);
public bool HasModule(string moduleName);
public IModuleHandler? GetHandler(string moduleName);
public bool LoadModuleByName(string moduleName, string? modulesRootPath = null);
public void LoadExternalModules(string? modulesRootPath = null);
public IReadOnlyList<string> GetAllCommands(string moduleFilter = "*");
```

---

## 12. CommandExecutor

- **命名空間**：`ARCOS.Core.Execution`
- **原始碼路徑**：[`src/ARCOS.Core/Execution/CommandExecutor.cs`](../../src/ARCOS.Core/Execution/CommandExecutor.cs)
- **用途說明**：單步直譯核心，負責參數過濾與狀態轉換。

```csharp
public CommandExecutor(ModuleRegistry registry);
public ModuleRegistry Registry { get; }
public CommandResult ExecuteStep(ExecutionContext ctx);
public CommandResult ExecuteCommandLine(ExecutionContext ctx, string commandLine);
```

---

## 13. ArcosThreadVM

- **命名空間**：`ARCOS.Core.Execution`
- **原始碼路徑**：[`src/ARCOS.Core/Execution/ArcosThreadVM.cs`](../../src/ARCOS.Core/Execution/ArcosThreadVM.cs)
- **用途說明**：多執行緒虛擬機器調度中心。

```csharp
public ObservableCollection<ExecutionContext> MainThreads { get; } // 槽位 0~12
public ObservableCollection<ExecutionContext> SubThreads { get; }  // 槽位 90+
public CommandExecutor Executor { get; }
public void LoadProgram(int progNo, ProgramTable program);
public void StartAll();
public void PauseAll();
public void ResumeAll();
public void StopAll();
public void Step(int progNo);
public void ResetProgram(int progNo);
```

---

## 14. DriverManager

- **命名空間**：`ARCOS.Core.Drivers`
- **原始碼路徑**：[`src/ARCOS.Core/Drivers/DriverManager.cs`](../../src/ARCOS.Core/Drivers/DriverManager.cs)
- **用途說明**：硬體驅動生命週期與通訊票據分派。

```csharp
public DriverManager(BasicEngines engines, string? driversRootPath = null);
public void RegisterDriver(string category, string deviceId, IDriver driver);
public IDriver? GetDevice(string category, string deviceId);
public IReadOnlyList<string> GetAvailableDrivers(string category);
public void ShutdownAll();
```

---

## 15. DriverTicket

- **命名空間**：`ARCOS.Core.Drivers`
- **原始碼路徑**：[`src/ARCOS.Core/Drivers/DriverTicket.cs`](../../src/ARCOS.Core/Drivers/DriverTicket.cs)
- **用途說明**：弱耦合硬體通訊票據。

```csharp
public class DriverTicket
{
    public string Command { get; }
    public IReadOnlyDictionary<string, string> Parameters { get; }
    public object? Payload { get; set; }
    public object? Result { get; set; }
    public DriverTicketStatus Status { get; set; }
    public bool IsSuccess => Status == DriverTicketStatus.Completed;
    public string? ErrorMessage { get; set; }

    public DriverTicket(string command, IDictionary<string, string>? parameters = null, object? payload = null);
}
```

---

## 16. IDriver

- **命名空間**：`ARCOS.Core.Drivers`
- **原始碼路徑**：[`src/ARCOS.Core/Drivers/IDriver.cs`](../../src/ARCOS.Core/Drivers/IDriver.cs)
- **用途說明**：硬體驅動介面。

```csharp
public interface IDriver : IDisposable
{
    string DeviceName { get; }
    string Category { get; }
    string DriverType { get; }
    void Init(string deviceName, string param, BasicEngines engines);
    void Shutdown();
    void PostTicket(DriverTicket ticket);
    object? CreateDriverView(); // 回傳 Avalonia Control 或 null
}
```

---

## 17. CsviPackage

- **命名空間**：`ARCOS.Core.Csvi`
- **原始碼路徑**：[`src/ARCOS.Core/Csvi/CsviPackage.cs`](../../src/ARCOS.Core/Csvi/CsviPackage.cs)
- **用途說明**：CSVI 4-in-1 虛擬儀器套件解包器。

```csharp
public class CsviPackage
{
    public CsviManifest Manifest { get; set; }
    public Assembly? LoadedAssembly { get; set; }
    public Dictionary<string, string> EmbeddedSourceFiles { get; set; }
    public Dictionary<string, string> EmbeddedRuntimeXaml { get; set; }
    public byte[] AssemblyRawBytes { get; set; }

    public static CsviPackage Load(string filePath);
    public static CsviPackage LoadFromFile(string filePath, string? password = null, bool unpackSources = true);
    public static CsviPackage LoadFromBytes(byte[] csviBytes, string? password = null, bool unpackSources = true);
}
```

---

## 18. ICallCommand

- **命名空間**：`ARCOS.Core.Execution`
- **原始碼路徑**：[`src/ARCOS.Core/Execution/ICallCommand.cs`](../../src/ARCOS.Core/Execution/ICallCommand.cs)
- **用途說明**：`ExternalModules/` 輕量單檔 CSVI 指令介面。

```csharp
public interface ICallCommand
{
    object? Execute(IReadOnlyList<string> args, ExecutionContext ctx);
}
```

---

## 19. IParameterGuidance

- **命名空間**：`ARCOS.Core.Guidance`
- **原始碼路徑**：[`src/ARCOS.Core/Guidance/IParameterGuidance.cs`](../../src/ARCOS.Core/Guidance/IParameterGuidance.cs)
- **用途說明**：參數導引對話框標準介面。

```csharp
public interface IParameterGuidance
{
    string CommandKey { get; }
    Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null);
}
```

---

## 20. ArcosPaths

- **命名空間**：`ARCOS.Core.Common`
- **原始碼路徑**：[`src/ARCOS.Core/Common/ArcosPaths.cs`](../../src/ARCOS.Core/Common/ArcosPaths.cs)
- **用途說明**：系統標準檔案與目錄路徑搜尋與解析器。

```csharp
public static class ArcosPaths
{
    public static string RootDirectory { get; }
    public static string ModulesDirectory { get; }
    public static string ExternalModulesDirectory { get; }
    public static string WaveformDirectory { get; }
    public static string PointAndPathDirectory { get; }
    public static string LogsDirectory { get; }

    public static string ResolveUserPanelCsviPath(string fileNameOrPath);
    public static string ResolveExternalModuleCsviPath(string targetName);
    public static string ResolveProPath(string proFileName);
    public static string ResolveInitPath(string initFileName);
    public static string GetTodayLogFilePath();
}
```

---

## 21. ScriptSerializer

- **命名空間**：`ARCOS.Core.Serialization`
- **原始碼路徑**：[`src/ARCOS.Core/Serialization/ScriptSerializer.cs`](../../src/ARCOS.Core/Serialization/ScriptSerializer.cs)
- **用途說明**：專案與腳本檔案讀寫工具。

```csharp
public static class ScriptSerializer
{
    public static List<ProgramTable> LoadProFile(string filePath);
    public static void SaveProFile(string filePath, IEnumerable<ProgramTable> programs);
    public static ProgramTable LoadInitFile(string filePath, int progNo = 0, string? progName = null);
    public static void SaveInitFile(string filePath, ProgramTable program);
}
```
