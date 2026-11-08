# 07. ExternalModules 單檔外掛開發 (Extend External CSVI)

對於不需要建立整套大型驅動與子系統的輕量級自訂功能（如：特定的校正演算法、數學運算、條碼檢查、外部 API 呼叫），ARCOSharp 提供了最便捷的擴充管道：**`ExternalModules/` 單檔 CSVI 外掛**。

---

## 1. 完整模組 vs. 單檔 External CSVI 選型比較

| 評估維度 | 完整功能模組 (`MODULES/<Name>/`) | 單檔外掛 (`ExternalModules/*.csvi`) |
|---|---|---|
| **指令語法** | `<Name>@<Action>` (如 `IO@SetPort`) | `Call@<Name>` 或 `Call@<Name>.csvi` |
| **實作介面** | `IModuleHandler` | `ICallCommand` (或相容 `IModuleHandler`) |
| **複雜度** | 高（含 Driver、多動作、狀態機） | 低（單一演算法或功能函式） |
| **部署門檻** | 需建獨立目錄結構與相依項 | 單一 `.csvi` 檔案丟入 `ExternalModules/` 即可 |
| **傳回值** | 透過 `CommandResult` 狀態機 | 直接 `return object?`，自動指派進變數表 |

---

## 2. ICallCommand 介面契約

放置於 `ExternalModules/` 下的 CSVI 核心類別必須實作 `ARCOS.Core.Execution.ICallCommand`：

```csharp
namespace ARCOS.Core.Execution;

public interface ICallCommand
{
    /// <summary>
    /// 執行 CSVI 自訂演算法或外部邏輯
    /// </summary>
    /// <param name="args">輸入參數字串清單 (已完成 Param Filter 變數替換)</param>
    /// <param name="ctx">執行緒即時上下文 (可訪問變數表、影像引擎、點位、IO 等)</param>
    /// <returns>回傳數值或物件 (若呼叫端有指定接收變數，自動寫入 VariableEngine)</returns>
    object? Execute(IReadOnlyList<string> args, ExecutionContext ctx);
}
```

---

## 3. 指令呼叫模式與傳回值自動注入 (Call Suffix Modes)

在 Sequencer 或腳本中調用時支援三種後綴模式：

| 後綴語法 | 模式名稱 | 特性與行為說明 |
|---|---|---|
| `Call@MyCalc 10,20` | **Debug 模式** | 標準循序調用，記錄詳細調用日誌。 |
| `Call@MyCalc& 10,20` | **Runtime 模式** | 高速執行，精簡非必要日誌。 |
| `Call@MyCalc&& 10,20`| **Reentrant 模式**| 可重入模式，保證多執行緒同時呼叫時之執行緒隔離。 |

### 傳回值自動寫入變數
若調用時最後一個位置參數為 `[VariableName]`，或以命名參數指定 `_OutVar=VarName`，核心 `CallModuleHandler` 會自動將 `Execute()` 的回傳值（如數字、字串）寫入 `VariableEngine`：
```
Call@MyCalc    10,20,[SumResult]
```
執行後，變數 `SumResult` 即被自動指派為 `30`！

---

## 4. 從 0 到 1 完整範例 (MyCalc.csvi)

### 步驟 1：建立專案 `MyCalc.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>MyCalc</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\ARCOS.Core\ARCOS.Core.csproj" />
  </ItemGroup>
</Project>
```

### 步驟 2：實作 `MyCalcCommand.cs`
```csharp
using ARCOS.Core.Execution;

namespace ExternalModules.MyCalc;

public class MyCalcCommand : ICallCommand
{
    public object? Execute(IReadOnlyList<string> args, ExecutionContext ctx)
    {
        ctx.Log($"[MyCalc] Received {args.Count} arguments: [{string.Join(", ", args)}]");

        if (args.Count < 2)
        {
            ctx.Log("[MyCalc] Error: At least 2 numbers required.", isError: true);
            return null;
        }

        double a = double.TryParse(args[0], out double v1) ? v1 : 0;
        double b = double.TryParse(args[1], out double v2) ? v2 : 0;
        double sum = a + b;

        ctx.Log($"[MyCalc] Calculated sum: {a} + {b} = {sum}");
        return sum; // 自動寫入回傳接收變數
    }
}
```

### 步驟 3：打包與部署
1. 編譯專案：`dotnet build -c Release`
2. 將編譯輸出之 `MyCalc.dll` 改名為 `MyCalc.csvi`（或透過 CsviStudio 打包成包含原始碼之 4-in-1 套件）。
3. 將 `MyCalc.csvi` 複製至發行拓撲中的：
   `dist/ARCOS_Runtime/ExternalModules/MyCalc.csvi`
4. 於排程器輸入指令並執行：
   ```
   Variable@Set     NumA,15
   Variable@Set     NumB,25
   Call@MyCalc      [NumA],[NumB],[Total]
   System@Log Message   Total is [Total]
   ```
5. 執行驗證，日誌將正確輸出 `Total is 40`！

---

## 5. 管理中心與工具鏈整合 (CALL.csvi & CsviStudio)

在主系統的「統一模組中心 (Modules Info)」中，所有外部單檔 CSVI 均由 **`CALL` 模組** 進行生命週期管理與監控：

1. **命名正交對稱**：原 `ExternalModule` 正式統一更名為 **`CALL`**，與排程器指令前綴 `Call@` 達成 100% 領域語言對齊。
2. **專屬前面板 `CALL.csvi`**：
   - 點擊 `Modules Info` 中的 `CALL`，即載入 `CALL.csvi` 面板。
   - 介面自動條列 `ExternalModules/` 目錄下的所有已編譯 `.csvi` 檔案。
   - 即時顯示各小程式的調用統計、耗時微秒（Latency）、回傳數值與最新例外。
   - 提供互動式除錯面板（Interactive Test Harness），可手動輸入參數測試執行。
3. **無縫串接 CsviStudio**：
   - `CALL.csvi` 提供快捷按鈕直接啟動或內嵌 **`CsviStudio`**。
   - 受益於 .NET 8 `CollectiblePluginContext` 不鎖檔機制，工程師可在 CsviStudio 內直接撰寫 C# 代碼、設計 FrontPanel、編譯產出 `.csvi`，系統即時熱加載生效，形成「撰寫 ➔ 編譯 ➔ 測試 ➔ 調度」的零割裂閉環！

