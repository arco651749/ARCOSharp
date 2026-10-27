# MVCON 模組完整開發規格書與 CSVI 範本代碼指南 (CSVI Module Development Specification & Template Guide)

> **版本**：2.0 (Grand Unified CSVI Edition)  
> **適用平台**：MVCON SHARP (C# .NET 8 / Avalonia UI)  
> **核心架構**：CSVI 四位一體統一虛擬儀表 (Headless Execution + Parameter Guidance Wizard + Live Debug Visualizer + SubPanel/Driver HMI)  
> **目標讀者**：設備軟體工程師、自動化模組開發者、硬體驅動外掛開發者

---

## 一、 模組架構概述與核心哲學 (Architectural Philosophy)

### 1.1 MVCON 的核心哲學：純粹的 Host 支架骨架 (Host Scaffold)
MVCON SHARP 本體被定義為 **「純粹的 UI 支架與流程直譯引擎」**。主程式只負責：
- 10 大底部分頁導覽切換
- 0~12 號平行執行緒虛擬機 (Thread VM)
- 指令直譯調度器 (`CommandExecutor / cmdexecute`)
- 系統基礎引擎池 (`BasicEngines`: IO, Variable, Point, Image, Daq, Timer)
- 人機動態子面板容器 (`SubPanel Host`)

所有具體的機台動作、硬體通訊、參數編輯，**100% 透過動態探索的 `.csvi` 虛擬儀表載入**。

### 1.2 CSVI 四位一體：單一組件包辦全部生命週期
在 MVCON SHARP 中，每一個指令與驅動都是一個 `.csvi`（C# Virtual Instrument）：
- **純執行 (Headless Execution)**：`Execute(...)` 提供微秒級 Native C# 執行速度，0ms UI 開銷。
- **參數導引視窗 (Parameter Guidance Wizard)**：`ShowGuidanceDialogAsync(...)` 於表格雙擊時彈出，動態感知硬體點位，防呆編輯。
- **即時運算可視化 (Live Debug Visualizer)**：`NotifyExecution(...)` 即時將計算過程推播至介面。
- **SubPanel 與 Driver HMI (SubPanel Mounting)**：`GetFrontPanelControl()` 提供可重用的 Avalonia 控制項，可嵌入主程式 `User Tab` 儀表板或硬體驅動維護頁。

---

## 二、 模組標準目錄結構 (Clean CSVI Topology)

每個模組目錄採用目錄驅動設計，**徹底消除 `USERPARAMETEREDITOR` 冗餘資料夾**：

```text
MODULES/<ModuleName>/
├── <Action1>.csvi                    # 指令 1 (四位一體：純執行 + 導引視窗 + 前面板)
├── <Action2>.csvi                    # 指令 2
├── <Action3>.csvi                    # 指令 3
├── <ModuleName>.csvi                 # 模組多合一複合包 (備用)
├── <ModuleName>.Module.dll           # 模組智慧跳板 (實作 IModuleHandler)
└── DRIVER/
    ├── <DriverA>.csvi                # 驅動 A (實作 IDriver + 驅動前面板)
    └── <DriverB>.csvi                # 驅動 B
```

### 2.1 目錄即功能 (Directory-Driven Discovery)
- **指令數量**：由 `MODULES/<ModuleName>/` 目錄下的 `*.csvi` 檔案數量決定。加一個 `.csvi` 就多一個指令，刪除就移除，無需重編主程式。
- **驅動數量**：由 `MODULES/<ModuleName>/DRIVER/` 目錄下的 `*.csvi` 決定。主程式 `Modules Info` 自動列出並加載對應面板。

---

## 三、 CSVI 指令開發標準範例

### 3.1 基礎類別範本 (`BaseParameterGuidance.cs`)

```csharp
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MVCON.Core.Drivers;
using MVCON.Core.Execution;
using MVCON.Core.Guidance;
using ExecutionContext = MVCON.Core.Execution.ExecutionContext;

namespace MVCON.Module.LaserMarking;

public abstract class BaseLaserGuidance : IParameterGuidance
{
    public abstract string CommandKey { get; }

    // 1. 參數導引視窗 (Front Panel Modal Dialog)
    public virtual async Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
    {
        var dialog = new LaserGuidanceDialog(CommandKey, currentParam, engines);
        if (owner is Window win)
        {
            return await dialog.ShowDialog<string?>(win);
        }
        dialog.Show();
        return null;
    }

    // 2. 輕量純邏輯執行
    public virtual bool Execute(BasicEngines engines, string parameter) => true;

    // 3. 全功能微步狀態機執行 (支援非同步等待)
    public virtual CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        bool ok = Execute(ctx.Engines, parameter);
        _sharedVm?.NotifyExecution(action, parameter, ctx.Engines);
        return ok ? CommandResult.Done() : CommandResult.Fail($"Execute returned false for {action}");
    }

    // 4. SubPanel / 即時前面板控制項
    protected static LaserFrontPanelView? _sharedFrontPanel;
    protected static LaserFrontPanelViewModel? _sharedVm;

    public virtual object? GetFrontPanelControl()
    {
        if (_sharedFrontPanel == null)
        {
            _sharedVm = new LaserFrontPanelViewModel();
            _sharedFrontPanel = new LaserFrontPanelView { DataContext = _sharedVm };
        }
        return _sharedFrontPanel;
    }
}
```

### 3.2 具體指令範例 (`StartMarkGuidance.cs`)

```csharp
public class LaserStartMarkGuidance : BaseLaserGuidance
{
    public override string CommandKey => "Laser@StartMark";

    public override CommandResult Execute(ExecutionContext ctx, string action, string parameter)
    {
        // 支援微步等待雷射標記完成 (非阻塞)
        var parsed = ArgParaHelper.ParseKeyValueArgs(parameter, out var posArgs);
        string profile = posArgs.Count > 0 ? posArgs[0] : "Default";

        if (ctx.State.Value == 0)
        {
            var driver = ctx.Drivers.GetDriver("Laser", "KeyenceMDV") as ILaserDriver;
            driver?.TriggerMark(profile);
            ctx.State.Value = 1;
            ctx.State["startTicks"] = Environment.TickCount64;
            return CommandResult.Wait();
        }

        long startTicks = ctx.State["startTicks"]?.GetValue<long>() ?? Environment.TickCount64;
        var laser = ctx.Drivers.GetDriver("Laser", "KeyenceMDV") as ILaserDriver;
        
        if (laser != null && laser.IsMarkFinished())
        {
            ctx.State.Reset();
            return CommandResult.Done();
        }

        if (Environment.TickCount64 - startTicks > 10000) // 10 秒逾時
        {
            ctx.State.Reset();
            return CommandResult.Fail($"Laser mark timeout for profile {profile}");
        }

        return CommandResult.Wait();
    }
}
```

---

## 四、 CSVI 硬體驅動開發標準範例

驅動同樣編譯為 `.csvi` 並置於 `MODULES/<ModuleName>/DRIVER/`：

```csharp
using MVCON.Core.Drivers;

namespace MVCON.Driver.Laser;

public class KeyenceLaserDriver : IDriver
{
    public string DeviceType => "Laser";
    public string DeviceName { get; private set; } = "KeyenceLaser";

    public bool Init(string deviceName, string configParams, BasicEngines engines)
    {
        DeviceName = deviceName;
        // 連線雷射標刻機硬體
        return true;
    }

    public void Shutdown()
    {
        // 安全斷線
    }

    public void PostTicket(DriverTicket ticket)
    {
        // 處理非同步指令票證
    }

    // 提供 Avalonia 原生驅動前面板
    public object? CreateDriverView()
    {
        return new KeyenceLaserHmiView(this);
    }
}
```

---

## 五、 自動編譯與發布規範 (`pack_runtime.ps1`)

模組與指令發布時，直接將輸出重定向至 `dist/MVCON_Runtime/MODULES/<Module>/`，並以 `.csvi` 命名：

```powershell
# 發布指令 CSVI
Copy-Item "$buildOut\Laser.dll" "$OutputDir\MODULES\Laser\StartMark.csvi" -Force
Copy-Item "$buildOut\Laser.dll" "$OutputDir\MODULES\Laser\SelectProfile.csvi" -Force

# 發布驅動 CSVI
Copy-Item "$driverOut\KeyenceLaser.dll" "$OutputDir\MODULES\Laser\DRIVER\KeyenceLaser.csvi" -Force
```

如此即可達成產線 100% 隨插即用、零重編主程式的最高架構彈性！
