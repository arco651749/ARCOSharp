# 05. 動態模組擴充教學 (Extend Module)

本指南指引開發者如何自訂一個全新的系統級模組（如 `LASER`、`RFID`、`PLC` 或 `DEMO`），並讓 ARCOSharp 在開機時自動掃描掛載，或透過指令 `System@IncludeModule` 在執行期動態隨選載入。

---

## 1. 模組組成與目錄結構規範 (Module Anatomy)

一個標準的 ARCOS 模組必須符合**約定優於配置 (Convention over Configuration)** 拓撲結構，置於 `MODULES/<ModuleName>/` 目錄下：

```
MODULES/DEMO/
├── DEMO.Module.dll             # 核心指令直譯組件 (實作 IModuleHandler)
├── Init.csvi                   # 模組初始化自解壓虛擬儀表 (System@IncludeModule 探測目標)
├── DRIVER/                     # 專屬硬體驅動隔離目錄
│   └── MockDemoDriver/
│       ├── MockDemoDriver.csvi
│       └── MockDemoDriver.Driver.dll
└── USERPARAMETEREDITOR/        # 參數導引對話框插件
    └── DoAction.Guidance.dll   # 實作 IParameterGuidance
```

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

當腳本執行到 `System@IncludeModule DEMO` 時，主程式核心經歷以下流程：
1. **釋放舊模組**：若模組已存在，若其實作 `IDisposable` 則調用 `Dispose()`，或調用其 `Execute("Reset", "", ctx)`。
2. **動態載入組件**：呼叫 `ctx.Registry.LoadModuleByName("DEMO")` 搜尋並加載 `DEMO.Module.dll`。
3. **探測 Init.csvi**：系統自動搜尋 `MODULES/DEMO/Init.csvi` 或 `init.csvi`。
4. **觸發初始化**：若存在 `Init.csvi` 且模組支援 `Init` 動作，自動調用：
   ```csharp
   handler.Execute("Init", "", ctx);
   ```

---

## 5. 從 0 到 1 完整範例 (Step-by-Step)

### 步驟 1：建立專案檔 `ARCOS.Module.Demo.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>DEMO.Module</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\ARCOS.Core\ARCOS.Core.csproj" />
  </ItemGroup>
</Project>
```

### 步驟 2：實作 `DemoModuleHandler.cs`
```csharp
using ARCOS.Core.Execution;

namespace ARCOS.Module.Demo;

public class DemoModuleHandler : IModuleHandler
{
    public string ModuleName => "DEMO";

    public IReadOnlyList<string> GetSupportedCommands() => new[]
    {
        "Init",
        "SetPower",
        "WaitReady",
        "Reset"
    };

    public CommandResult Execute(string action, string filteredParam, ExecutionContext ctx)
    {
        switch (action.ToUpperInvariant())
        {
            case "INIT":
                ctx.Log("[DEMO] Initializing hardware driver...");
                ctx.Engines.Variable.Set("Demo_Status", "INITIALIZED", "DEMO");
                return CommandResult.Done();

            case "SETPOWER":
                // 參數格式：<State:0|1>
                bool on = filteredParam.Trim() == "1";
                ctx.Engines.Io.SetPort("DO_DemoPower", on);
                ctx.Log($"[DEMO] Power set to {on}");
                return CommandResult.Done();

            case "WAITREADY":
                if (ctx.State.Step == 0)
                {
                    ctx.State.TimerMs = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                    ctx.State.Step = 1;
                    return CommandResult.Wait();
                }
                if (ctx.Engines.Io.GetPort("DI_DemoReady"))
                {
                    return CommandResult.Done();
                }
                long elapsed = (DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond) - ctx.State.TimerMs;
                if (elapsed > 3000) return CommandResult.Fail("DEMO hardware failed to ready within 3s!");
                return CommandResult.Wait();

            case "RESET":
                ctx.Engines.Io.SetPort("DO_DemoPower", false);
                return CommandResult.Done();

            default:
                return CommandResult.Fail($"Unknown DEMO action '{action}'");
        }
    }
}
```

### 步驟 3：編譯與部署
1. 編譯專案輸出：`dotnet build -c Release`
2. 將產生之 `DEMO.Module.dll` 複製到發布目錄：
   `dist/ARCOS_Runtime/MODULES/DEMO/DEMO.Module.dll`
3. 建立空的或含有初始化邏輯的 `Init.csvi` 置於該目錄。
4. 啟動 ARCOSharp，在主程式或 Test Thread Editor 輸入：
   ```
   System@IncludeModule    DEMO
   DEMO@SetPower           1
   DEMO@WaitReady          
   ```
5. 執行驗證，控制台將顯示正確的日誌輸出！
