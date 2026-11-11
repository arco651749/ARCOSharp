# 05. 動態模組擴充教學 (Extend Module)

本指南指引開發者如何自訂一個全新的系統級模組（如 `LASER`、`RFID`、`PLC` 或 `DEMO`），並讓 ARCOSharp 在開機時自動掃描掛載，或透過指令 `System@IncludeModule` 在執行期動態隨選載入。

---

## 1. 模組組成與目錄結構規範 (Module Anatomy)

在 **ARCOSharp CSVI 3.0 純 CSVI 架構**中，擴充任何新模組**完全不需要修改 ARCOSharp 主程式或 `ARCOS.Core`，也不需要建立 `.csproj` 編譯 `<Module>.Module.dll` 或撰寫獨立的 `<Module>ModuleHandler.cs`**。

一個標準的 ARCOS 純 CSVI 模組遵循**約定優於配置 (Convention over Configuration)**，直接置於 `MODULES/<ModuleName>/` 目錄下：

```
MODULES/DEMO/
├── DEMO.csvi                   # 【核心主控跳板】與模組同名的主 CSVI (實作 IModuleHandler + IParameterGuidance)
│                               #  對內：動態掃描並快取派發同目錄下的 <Action>.csvi
│                               #  對外：提供 Modules Info 一級模組前面板視圖 (Panel.axaml)
├── Init.csvi                   # (選配) 模組初始化指令 CSVI (當執行 System@IncludeModule::DEMO 時自動觸發)
├── SetPower.csvi               # 【單一指令 CSVI】DEMO@SetPower (內含 Logic.cs + Wizard.axaml + Panel.axaml)
├── WaitReady.csvi              # 【單一指令 CSVI】DEMO@WaitReady
└── DRIVER/                     # (選配) 專屬硬體驅動隔離目錄
    └── MockDemoDriver/
        └── MockDemoDriver.csvi # 主動式硬體驅動封裝 (實作 IDriver)
```

> [!IMPORTANT]
> 1. **「主程式不動、全靠 CSVI」原則**：
>    - 每個模組目錄下與模組同名的 `<Module>.csvi`（例如 `MODULES/IO/IO.csvi`、`MODULES/POINT&PATH/Point&Path.csvi`、`MODULES/HELLO/Hello.csvi`、`MODULES/DEMO/DEMO.csvi`）直接擔任該模組的 `IModuleHandler` 主控跳板。
>    - `ModuleRegistry` 在啟動或執行 `System@IncludeModule` 時，會直接載入 `MODULES/<Module>/<Module>.csvi` 註冊為該模組的 Handler。
> 2. **廢除舊版 `USERPARAMETEREDITOR` 與 `*.Module.dll`**：
>    - 每個指令的執行邏輯 (`Logic.cs`)、參數導引視窗 (`Wizard.axaml`) 與單步測試面板 (`Panel.axaml`) 皆已 4-in-1 封裝於 `MODULES/<Module>/<Action>.csvi` 中，嚴禁再建立舊式 `USERPARAMETEREDITOR/` 子目錄或冗餘的 `*ModuleHandler.cs`。
> 3. 關於模組前面板 `DEMO.csvi` 如何於「統一模組中心 (Modules Info)」中呈現，以及如何實現下轄多裝置驅動的「兩層式 SubPanel 路由」，請參閱：[12. 統一模組中心與兩層式 SubPanel 規格書](./12_UNIFIED_MODULES_INFO_SPEC.md) 與 [13. 完整開發聖經](./13_COMPLETE_MODULE_AND_CSVI_DEV_GUIDE.md)。


---

## 2. 模組處理器介面契約 (IModuleHandler)

模組主程式必須實作 `ARCOS.Core.Execution.IModuleHandler`：

```csharp
namespace ARCOS.Core.Execution;

public interface IModuleHandler
{
    // 模組識別碼 (大小寫不敏感，如 "DEMO")
    string ModuleName { get; }

    // 支援的指令清單 (供 ComSelectWindow 探索與自動補全)
    IReadOnlyList<string> GetSupportedCommands();

    // 單步執行分派邏輯
    CommandResult Execute(string action, string filteredParam, ExecutionContext ctx);
}
```

---

## 3. 微步非阻塞狀態機開發 (Micro-stepping Pattern)

> [!CRITICAL]
> **絕對禁止在 `Execute()` 中呼叫 `Thread.Sleep()` 或任何同步阻塞呼叫！**
> 任何耗時或需要等待的動作（如等待硬體感測器、延遲、等待馬達定位），必須使用 `ctx.State` 微步暫存器，並回傳 `CommandResult.Wait()`。

### 推薦微步狀態機模式：

```csharp
public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
{
    if (action.Equals("WaitSensor", StringComparison.OrdinalIgnoreCase))
    {
        // 狀態 0：初始化階段
        if (ctx.State.Step == 0)
        {
            ctx.State.TimerMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            ctx.State.Step = 1; // 推進至等待狀態
            ctx.Log($"[DEMO] Started waiting for sensor. Timeout: {filteredParam}s");
            return CommandResult.Wait();
        }

        // 狀態 1：週期性輪詢階段
        if (ctx.State.Step == 1)
        {
            // 1. 檢查硬體條件
            if (ctx.Engines.Io.GetPort("Sensor_Ready"))
            {
                ctx.Log("[DEMO] Sensor is ON! Condition met.");
                return CommandResult.Done(); // 完成，PC + 1，State 自動歸零
            }

            // 2. 檢查逾時 (假設參數為秒數)
            double timeoutSec = double.TryParse(filteredParam, out double s) ? s : 5.0;
            long nowMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            if ((nowMs - ctx.State.TimerMs) > (timeoutSec * 1000))
            {
                return CommandResult.Fail($"[DEMO] WaitSensor timed out after {timeoutSec}s!");
            }

            // 條件尚未滿足，交還 CPU 執行權
            return CommandResult.Wait();
        }
    }

    return CommandResult.Done();
}
```

---

## 4. `Init.csvi` 的載入與執行機制

當腳本執行到 `System@IncludeModule::DEMO` 時，主程式核心經歷以下流程：
1. **釋放舊模組**：若模組已存在，若其實作 `IDisposable` 則調用 `Dispose()`，或調用其 `Execute("Reset", "", ctx)`。
2. **動態載入主控 `<Module>.csvi`**：呼叫 `ctx.Registry.LoadModuleByName("DEMO")` 搜尋並加載 `MODULES/DEMO/DEMO.csvi`（實作 `IModuleHandler`）。
3. **探測 `Init.csvi`**：系統自動檢查 `MODULES/DEMO/Init.csvi` 是否存在，且 `DEMO.csvi` 的 `GetSupportedCommands()` 包含 `"Init"`。
4. **觸發初始化**：自動調用：
   ```csharp
   handler.Execute("Init", "", ctx); // 由 DEMO.csvi 自動轉發給 MODULES/DEMO/Init.csvi 執行
   ```

---

## 5. 從 0 到 1 建立純 CSVI 模組完整範例 (Step-by-Step)

### 步驟 1：撰寫模組同名主控 `DEMO.csvi` 的 `Logic.cs`
`DEMO.csvi` 同時實作 `IParameterGuidance` 與 `IModuleHandler`，內部自帶檔案時間戳快取，自動掃描並將 `<Action>` 轉發給同目錄下的 `<Action>.csvi`：

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ARCOS.Core.Csvi;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.Demo;

public class DemoMasterGuidance : IParameterGuidance, IModuleHandler
{
    public string CommandKey => "DEMO";
    public string ModuleName => "DEMO";

    private static readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, IModuleHandler Handler)> _commandCache
        = new(StringComparer.OrdinalIgnoreCase);

    private static string? ResolveModuleDirectory()
    {
        string asmLoc = typeof(DemoMasterGuidance).Assembly.Location;
        if (!string.IsNullOrEmpty(asmLoc))
        {
            string? dir = Path.GetDirectoryName(asmLoc);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
        }
        return ModuleRegistry.ResolveModuleDirectoryPath("DEMO");
    }

    public IReadOnlyList<string> GetSupportedCommands()
    {
        string? dir = ResolveModuleDirectory();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return Array.Empty<string>();

        // 掃描同目錄下所有 <Action>.csvi（排除與模組同名的 DEMO.csvi，但保留 Init.csvi 供 IncludeModule 使用）
        return Directory.EnumerateFiles(dir, "*.csvi", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name)
                        && !name.Equals(ModuleName, StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("UserPanel", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(action))
            return CommandResult.Fail("DEMO action cannot be empty.");

        string? dir = ResolveModuleDirectory();
        if (string.IsNullOrEmpty(dir))
            return CommandResult.Fail("DEMO module directory not found.");

        string? csviPath = Directory.EnumerateFiles(dir, "*.csvi", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), action.Trim(), StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(csviPath) || !File.Exists(csviPath))
            return CommandResult.Fail($"DEMO command CSVI not found: MODULES/DEMO/{action}.csvi");

        DateTime lastWrite = File.GetLastWriteTimeUtc(csviPath);
        if (!_commandCache.TryGetValue(csviPath, out var cached) || cached.LastWriteUtc != lastWrite)
        {
            var pkg = CsviPackage.LoadFromFile(csviPath, password: null, unpackSources: false);
            var handlerType = pkg.LoadedAssembly?.GetExportedTypes()
                .FirstOrDefault(t => !t.IsAbstract && !t.IsInterface && typeof(IModuleHandler).IsAssignableFrom(t));
            if (handlerType == null || Activator.CreateInstance(handlerType) is not IModuleHandler h)
                return CommandResult.Fail($"Failed to instantiate IModuleHandler from '{csviPath}'.");
            cached = (lastWrite, h);
            _commandCache[csviPath] = cached;
        }

        return cached.Handler.Execute(action, filteredParam, ctx);
    }

    public bool Execute(BasicEngines engines, string parameter) => true;

    public Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
        => Task.FromResult<string?>(currentParam);

    public object? GetFrontPanelControl()
    {
        using var stream = typeof(DemoMasterGuidance).Assembly.GetManifestResourceStream("CSVI.RuntimeXaml.Panel.axaml");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return AvaloniaRuntimeXamlLoader.Parse<Control>(reader.ReadToEnd());
    }
}
```

### 步驟 2：撰寫單一指令 `SetPower.csvi` 的 `Logic.cs`
每個指令各自獨立為一個 `<Action>.csvi`（如 `SetPower.csvi`、`WaitReady.csvi`、`Init.csvi`）：

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ARCOS.Core.Drivers;
using ARCOS.Core.Execution;
using ARCOS.Core.Guidance;
using ExecutionContext = ARCOS.Core.Execution.ExecutionContext;

namespace ARCOS.Module.Demo;

public class DemoSetPowerGuidance : IParameterGuidance, IModuleHandler
{
    public string CommandKey => "DEMO@SetPower";
    public string ModuleName => "DEMO";
    public IReadOnlyList<string> GetSupportedCommands() => new[] { "SetPower" };

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        bool on = filteredParam.Trim() == "1" || filteredParam.Trim().Equals("On", StringComparison.OrdinalIgnoreCase);
        ctx.Engines.Io.SetPort("DO_DemoPower", on);
        ctx.Log($"[DEMO@SetPower] Power set to {on}");
        return CommandResult.Done();
    }

    public bool Execute(BasicEngines engines, string parameter) => true;
    public Task<string?> ShowGuidanceDialogAsync(string currentParam, BasicEngines engines, object? owner = null)
        => Task.FromResult<string?>(string.IsNullOrWhiteSpace(currentParam) ? "1" : currentParam);
    public object? GetFrontPanelControl() => null;
}
```

### 步驟 3：使用內嵌 Roslyn CLI 編譯並部署
使用 `CSVI_Studio.dll --compile` 直接將來源資料夾編譯為 `.csvi`（詳見 [13. 完整開發聖經 第 7 節](./13_COMPLETE_MODULE_AND_CSVI_DEV_GUIDE.md#7-如何-build-csvi四大編譯與打包管道詳解)）：
```powershell
& "D:\Users\arco0\.dotnet\dotnet.exe" "d:\AntiGravity\ARCOSharp\src\ARCOS.CsviStudio\bin\Release\net8.0\CSVI_Studio.dll" `
    --compile "d:\temp\Demo_Master_Src" "d:\AntiGravity\ARCOSharp\src\MODULES\DEMO\DEMO.csvi"

& "D:\Users\arco0\.dotnet\dotnet.exe" "d:\AntiGravity\ARCOSharp\src\ARCOS.CsviStudio\bin\Release\net8.0\CSVI_Studio.dll" `
    --compile "d:\temp\Demo_SetPower_Src" "d:\AntiGravity\ARCOSharp\src\MODULES\DEMO\SetPower.csvi"
```
接著啟動 ARCOSharp，在排程器或 Test Thread Editor 輸入：
```
System@IncludeModule    DEMO
DEMO@SetPower           1
```
無需重編任何主程式 DLL，即可直接載入並執行！
