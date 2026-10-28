# MVCON SHARP CSVI 虛擬儀表全局架構與動態探索規格書 (Grand Unified CSVI Architecture Spec)

> **版本**：2.0 (Grand Unified CSVI Edition)  
> **更新日期**：2026-10-27  
> **核心哲學**：一組件四面向 (One Assembly, Four Facets)、目錄即指令 (Directory-Driven Discovery)、零冗餘目錄 (Zero Legacy Folders)。

---

## 一、 核心哲學：CSVI「四位一體」統一虛擬儀表 (Grand Unified CSVI)

在傳統 LabVIEW MVCON 架構中，每個功能被拆解為多個相互脫鉤的檔案：
- **執行端**：`CoreModules/IO Module/IO.vi`（龐大的 Case Structure）
- **編輯端**：`USERPARAMETEREDITOR/IO/IO@SetPort__Parameter.vi`（獨立的彈窗 VI）
- **驅動端**：`Drivers/IO/Advantech.vi`（獨立的驅動 VI）
- **人機端**：`User Tab` 上的各類子面板 (SubPanel)

這種分離導致多個致命痛點：
1. **多頭維護**：修改一個指令的參數定義，必須同時改動執行 Case、編輯 VI、呼叫端與設定檔。
2. **目錄雜亂**：充斥著專門放彈窗的 `USERPARAMETEREDITOR/`，造成檔案重複與版本不同步。
3. **無熱插拔**：模組無法像現代微服務一樣動態熱插拔或非鎖檔替換。

### CSVI 終極方案：單一組件，四個面向 (1 Assembly, 4 Facets)

在 **MVCON SHARP** 中，每一個指令與驅動都是一個 `.csvi`（C# Virtual Instrument，實質為標準 .NET 動態組件）。**一個 `.csvi` 檔案，完美具備四種面向**：

```mermaid
flowchart TD
    subgraph CSVI ["io@SetPort.csvi (單一 C# 虛擬儀表)"]
        F1["【面向 1：純執行 (Headless Execution)】<br/>Execute(ctx, action, param)<br/>微秒級純 C# IL，0ms UI 開銷，產線高速量產"]
        F2["【面向 2：參數導引視窗 (Parameter Wizard)】<br/>ShowGuidanceDialogAsync(param, engines)<br/>雙擊表格參數即刻彈出，動態感知硬體下拉選單"]
        F3["【面向 3：即時運算視覺化 (Live Debug Visualizer)】<br/>NotifyExecution(action, param, engines)<br/>執行狀態、計算過程與耗時即時推播至面板"]
        F4["【面向 4：人機與驅動子面板 (SubPanel & Driver HMI)】<br/>GetFrontPanelControl()<br/>提供 Avalonia 原生控制項，可嵌入 User Tab 或作為 Driver 面板"]
    end

    Host["MVCON.App 主程式 (Host)"] -->|量產執行 (無 UI)| F1
    Host -->|表格雙擊編輯| F2
    Host -->|執行狀態連動| F3
    Host -->|動態掛載至 SubPanel| F4
```

1. **純執行 (Headless Execution)**：
   - 實作 `IParameterGuidance.Execute(...)`。
   - 在 Sequencer 自動化全速生產時，僅調用微秒級純邏輯，不啟動 UI、不配置視窗控制項、不佔用繪圖執行緒。
2. **參數導引視窗 (Parameter Guidance Wizard)**：
   - 實作 `IParameterGuidance.ShowGuidanceDialogAsync(...)`。
   - 支援 Zero Cognitive Load（零記憶負擔）原則：自動從 `BasicEngines` 抓取真實硬體埠號、軸代號、點位標籤，提供下拉選單與即時語法預覽。
3. **即時運算可視化 (Live Debug Calculation Visualizer)**：
   - 透過指令內部的事件匯流排 (`NotifyExecution`)，將即時運算步驟、通訊耗時、目前電位狀態反饋至 ViewModel。
4. **子面板嵌入與驅動 HMI (SubPanel Mounting & Driver UI)**：
   - 實作 `IParameterGuidance.GetFrontPanelControl()`，回傳 Avalonia 原生控制項。
   - 在主畫面 `User Tab` 的 `SubPanel` 中，可自由選擇掛載該指令的儀表板；同時該控制項亦能直接作為 `MODULES/<Module>/DRIVER/` 中硬體驅動的專屬控制面板。

---

## 二、 目錄驅動探索機制 (Directory-Driven Discovery)

MVCON SHARP 主程式**徹底廢除任何寫死的指令清單與驅動類別註冊碼**。主程式完全由實體檔案目錄驅動：

### 2.1 指令探索規則：目錄下有多少 `.csvi`，就有多少指令
- 探索目錄：`MODULES/<Module>/`（例如 `MODULES/IO/`）
- 規則：主程式啟動時，動態掃描目錄下的所有 `*.csvi` 檔案，自動解析組件並註冊支援指令。
- **完全消滅 `USERPARAMETEREDITOR` 目錄**：
  在 CSVI 架構下，指令本身就內建參數導引視窗，因此 **`MODULES/IO/USERPARAMETEREDITOR` 目錄完全廢止並移除**。所有的指令 `.csvi` 直接平鋪於 `MODULES/<Module>/` 根目錄下！

### 2.2 驅動探索規則：DRIVER 目錄下有多少 `.csvi`，就有多少 Driver
- 探索目錄：`MODULES/<Module>/DRIVER/`（例如 `MODULES/IO/DRIVER/`）
- 規則：`DriverManager` 啟動時掃描該目錄下的所有 `*.csvi` 與子資料夾，動態識別實作 `IIoDriver`、`IMotionDriver` 等介面的驅動。
- 在主程式的 `Modules Info` 分頁中，會自動列出發現的驅動清單，並動態調用 `GetFrontPanelControl()` 將驅動面板內嵌展示。

### 2.3 物理目錄結構範例

```text
dist/MVCON_Runtime/
├── MVCON.App.exe                  <-- 主程式 (純粹的 UI 支架骨架)
├── MODULES/
│   ├── IO/
│   │   ├── SetPort.csvi           <-- 指令 1: 內建純執行 + 導引視窗 + 前面板
│   │   ├── ClrPort.csvi           <-- 指令 2
│   │   ├── WaitPortOn.csvi        <-- 指令 3
│   │   ├── WaitPortOff.csvi       <-- 指令 4
│   │   ├── Flash(OnOff).csvi      <-- 指令 5
│   │   ├── Flash(OffOn).csvi      <-- 指令 6
│   │   ├── CheckLogicOn.csvi      <-- 指令 7
│   │   ├── CheckLogicOff.csvi     <-- 指令 8
│   │   ├── SetPortName.csvi       <-- 指令 9
│   │   ├── AddVirtualIO.csvi      <-- 指令 10
│   │   ├── AddIODriver.csvi       <-- 指令 11
│   │   ├── InitIOPort.csvi        <-- 指令 12
│   │   ├── ForceUpdate.csvi       <-- 指令 13
│   │   ├── Shift.csvi             <-- 指令 14
│   │   ├── IO.csvi                <-- 模組多合一複合包 (備援)
│   │   ├── IO.Module.dll          <-- IO 模組智慧跳板 (Gateway)
│   │   └── DRIVER/
│   │       └── MockIO.csvi        <-- 驅動 1: 實作 IIoDriver + 驅動前面板
│   ├── MOTION/
│   │   ├── GoPoint.csvi
│   │   ├── WaitMotor.csvi
│   │   ├── Motion.Module.dll
│   │   └── DRIVER/
│   │       ├── MockMotion.csvi
│   │       └── Fanuc.csvi
│   ├── CAPTURE/
│   └── DAQ/
```

---

## 三、 零鎖檔熱插拔架構 (Non-Locking Dynamic Loading)

Windows 原生載入 DLL 時（如 `Assembly.LoadFrom`）會強制佔用檔案句柄（File Handle Lock），導致產線運轉或程式開啟時無法覆蓋或升級組件。

MVCON SHARP 採用 **全位元組非鎖檔載入（`File.ReadAllBytes`）** 機制：

```csharp
// MVCON.Core / DriverManager.cs & IoModuleHandler.cs
byte[] assemblyBytes = File.ReadAllBytes(csviPath);
var assembly = Assembly.Load(assemblyBytes);
```

### 優勢：
1. **熱替換無阻礙**：機台運作時，工程師可直接編譯並覆蓋磁碟上的 `SetPort.csvi`，無需重啟 MVCON 主程式。
2. **FileSystemWatcher 自動重整**：模組閘道器監聽檔案變更事件，一旦磁碟上的 `.csvi` 更新，自動清空快取，下次調用時立即生效。

---

## 四、 核心介面契約規範 ([`IParameterGuidance.cs`](file:///d:/AntiGravity/MVCON2CSharp/src/MVCON.Core/Guidance/IParameterGuidance.cs))

所有 `.csvi` 虛擬儀表必須實作統一的 `IParameterGuidance` 契約：

```csharp
namespace MVCON.Core.Guidance;

public interface IParameterGuidance
{
    /// <summary>
    /// 指令關鍵字，例如 "IO@SetPort"
    /// </summary>
    string CommandKey { get; }

    /// <summary>
    /// 是否支援互動對話視窗
    /// </summary>
    bool SupportsInteractive => true;

    /// <summary>
    /// 【面向 2：參數導引視窗】
    /// 彈出參數設定視窗，動態獲取 BasicEngines 硬體清單，回傳格式化參數字串
    /// </summary>
    Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null);

    /// <summary>
    /// 【面向 1：純執行 (輕量版)】
    /// 供簡易調試或直接單步執行
    /// </summary>
    bool Execute(BasicEngines engines, string parameter) => true;

    /// <summary>
    /// 【面向 1：純執行 (全功能微步狀態機版)】
    /// 供 Sequencer 執行，支援 Delay、Timeout 與非同步微步等待
    /// </summary>
    CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        bool ok = Execute(ctx.Engines, parameter);
        return ok ? CommandResult.Done() : CommandResult.Fail($"Execute returned false for {action}");
    }

    /// <summary>
    /// 【面向 4：人機前面板 / 驅動 HMI】
    /// 取得 Avalonia 原生控制項，供主程式 SubPanel 容器動態嵌入
    /// </summary>
    object? GetFrontPanelControl() => null;
}
```

---

## 五、 主程式 SubPanel 與驅動 HMI 嵌入機制

主程式 `MainWindow.axaml` 提供了標準的動態容器 `SubPanel`：

```xml
<!-- MainWindow.axaml (User Tab) -->
<ContentControl x:Name="UserSubPanelHost" 
                Content="{Binding CurrentSubPanelControl}" 
                HorizontalAlignment="Stretch" 
                VerticalAlignment="Stretch"/>
```

切換掛載時，主程式呼叫 `ParameterGuidanceManager` 或 `DriverManager`：
```csharp
// 1. 掛載指令的 Front Panel
var ioGuidance = ParameterGuidanceManager.Instance.GetGuidance("IO@SetPort");
UserSubPanelHost.Content = ioGuidance?.GetFrontPanelControl();

// 2. 掛載驅動的 Driver HMI
var driver = DriverManager.Instance.GetDriver("IO", "MockIO");
UserSubPanelHost.Content = driver?.CreateDriverView();
```

這達成了真正的 **UI 複用性**：同一個 IO 面板控制項，既是手動測試按鈕，也是執行狀態指示燈，更是硬體驅動的維護人機。

---

## 六、 CSVI 3.0 可攜式原始碼封裝與獨立 `CSVI Studio` 工具鏈

為了徹底解決產線現場無網路、無 Visual Studio 時的即時微調需求，同時避免外掛函式庫版本衝突（DLL Hell），CSVI 架構進一步支援 **可選式內嵌原始碼（`.cs` + `.axaml`）** 與獨立的 **`CSVI_Studio.exe`** 開發調試工作站：

1. **PE 原生內嵌原始碼 (`ManifestResource`)**：
   - 保持 `.csvi` 為 100% 標準 .NET PE 組件（主程式 `Assembly.Load(byte[])` 零解壓開銷）。
   - 於 `.rsrc` 區段選擇性封裝 `CSVI.Manifest.json`、`CSVI.RuntimeXaml.*` 與 `CSVI.Source.Bundle`（含 `.cs` 邏輯與 `.axaml` 介面）。
2. **三段式安全封裝模式（使用者可選）**：
   - 🟢 **`PortableOpen`（開放攜帶模式）**：內嵌明文 `.cs` + `.axaml`，複製單一 `.csvi` 至任何工控機皆可用 `CSVI Studio` 開啟修改並現場重編。
   - 🟡 **`PasswordLocked`（密碼鎖定模式）**：以 AES-256-GCM 加密內嵌原始碼，主程式可正常執行，但於 `CSVI Studio` 修改源碼需輸入工程師密碼（對標 LabVIEW Block Diagram Password）。
   - 🔴 **`StrippedRelease`（純執行剝離模式）**：完全剝離 `.cs` 原始碼，僅保留編譯後 IL 與執行期 UI，保護核心演算法機密（對標 LabVIEW Remove Block Diagram）。
3. **獨立工具鏈工作站 (`CSVI_Studio.exe`)**：
   - 獨立於 `MVCON.App.exe` 之外的專用 IDE 與四位一體沙盒測試器（全面取代舊式 `GuidanceRunner.exe`）。
   - **完整內建 Toolchain（Roslyn C# 編譯器 + `AvaloniaRuntimeXamlLoader` + 標準鎖定版 SDK 參考庫）**：所有 `.csvi` 必須對齊 `CSVI Studio` 的函式庫與 Runtime ABI 進行編譯與校驗，徹底消滅版本不相容問題。

> 📄 **完整二進位規格與實作藍圖請參閱**：[`docs/CSVI_STUDIO_AND_PORTABLE_SPEC.md`](./CSVI_STUDIO_AND_PORTABLE_SPEC.md)
