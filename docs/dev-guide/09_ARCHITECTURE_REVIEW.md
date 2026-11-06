# 09. 架構瓶頸分析與審查報告 (Architecture Review)

本報告以客觀、批判性與嚴謹的軟體工程角度，深度審查 **ARCOSharp** 當前程式碼庫（`src/`）在架構設計、執行緒安全、動態外掛生命週期與工業自動化安全層面存在的潛在弱點與重構建議。

---

## 1. 核心架構問題診斷清單

| # | 嚴重度 | 缺陷類別 | 缺陷概述與程式碼證據 | 潛在後果與影響 | 建議改善方案 |
|---|---|---|---|---|---|
| **1** | **高 (High)** | 工控安全 | **遠端 TCP 與 MCP 自動化介面無認證**<br>證據：[`TelnetServer.cs:31`](../../src/ARCOS.Remoter/TelnetServer.cs#L31) 監聽 `0.0.0.0:9527`；[`ArcosMcpServer.cs:65`](../../src/ARCOS.App/Mcp/ArcosMcpServer.cs#L65) 監聽 `127.0.0.1:19528`。 | 區網內任何惡意程式或腳本皆可未經授權發送 `$SetIO`、`$Execute` 直接驅動汽缸、馬達或雷射，具備嚴重工安隱患。 | 1. 預設綁定 `IPAddress.Loopback`。<br>2. 增加 Token / API Key 鑑權機制。<br>3. 執行硬體動作前強制檢查安全門與急停旗標。 |
| **2** | **已修復 (Resolved)** | 記憶體與熱更 | **Default AssemblyLoadContext 載入外掛導致檔案鎖死與無法卸載**<br>原證據：[`ParameterGuidanceManager.cs`](../../src/ARCOS.App/Guidance/ParameterGuidanceManager.cs)、[`DriverManager.cs`](../../src/ARCOS.Core/Drivers/DriverManager.cs)。 | 所有動態載入的 `.csvi` 與 `.dll` 皆進入全域預設載入上下文，在 Windows 上造成實體檔案被鎖死，無法在不停機狀態下更新或抽換外掛。 | **已全面重構**：導入 [`PluginLoadManager`](../../src/ARCOS.Core/Common/PluginLoadContext.cs) 與 `CollectiblePluginContext`（`isCollectible: true`），全面採用非鎖檔串流讀取，支援檔案異動自動卸載舊上下文並熱更替換。 |
| **3** | **中 (Medium)** | 模組維護 | **主排程編輯器與測試執行緒邏輯大量重複**<br>證據：[`SequencerEditorControl.axaml.cs`](../../src/ARCOS.App/Controls/SequencerEditorControl.axaml.cs) 與 [`TestThreadWindow.axaml.cs`](../../src/ARCOS.App/Dialogs/TestThreadWindow.axaml.cs)。 | Undo/Redo 堆疊、多行選取演算法、剪貼簿貼入/搬移、IO 點位反向同步在兩份檔案中存在超過 80% 的代碼重複，易引發單邊修復回歸。 | 抽離出共享的 `SequencerDocumentService` 與 `SequencerEditingCoordinator`，讓兩者共享相同的編輯領域邏輯。 |
| **4** | **中 (Medium)** | 職責過度集中 | **MainWindow 上帝類別 (God Class)**<br>證據：[`MainWindow.axaml.cs`](../../src/ARCOS.App/MainWindow.axaml.cs) 超過 1,700 行。 | 檔案對話框挑選、子面板反射載入、執行緒生命週期廣播、日誌寫入與快捷鍵邏輯全部混於 Code-behind，違反單一職責原則。 | 拆分出 `UserPanelMountService`、`WorkspaceProjectManager` 與 `MachineStateCoordinator` 等獨立服務。 |
| **5** | **中 (Medium)** | 容錯穩定性 | **DriverTicket 調用若使用同步 Wait 缺乏硬體逾時防護**<br>證據：[`IDriver.PostTicket`](../../src/ARCOS.Core/Drivers/IDriver.cs) 雖為非阻塞，但在呼叫端若調用 `ticket.Wait()` 未設安全逾時。 | 若第三方寫的 Serial/Modbus 驅動在實體通訊斷線時未返回或長時間阻塞，將直接卡死整個 VM 執行緒。 | 呼叫端統一使用帶逾時看門狗之 `Wait(timeoutMs)` 或全面改為 `await ticket.Task` 配合 3 秒安全逾時。 |
| **6** | **低 (Low)** | 配置漂移 | **UserPanel.csvi 雙份部署導致版本漂移**<br>證據：根目錄 `UserPanel.csvi` 與 `MODULES/SYSTEM/UserPanel.csvi` 同時存在。 | 開發者更新了其中一份，另一份未同步，造成執行時期因搜尋路徑差異而載入舊版本面板。 | 統一收斂至單一權威路徑，發布腳本建立捷徑或符號連結。 |

---

## 2. 深度技術剖析與重構藍圖

### 2.1 外掛動態卸載沙箱化 (Plugin Isolation Architecture - 已實作完成)
過去使用 `AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath)` 或 `Assembly.Load(bytes)`，導致所有外掛進入非收集上下文且鎖死 Windows 實體檔案。

**現已完成之架構（參見 [`PluginLoadContext.cs`](../../src/ARCOS.Core/Common/PluginLoadContext.cs)）**：
- **`CollectiblePluginContext`**：繼承自 `AssemblyLoadContext` 並設定 `isCollectible: true`，採用純記憶體位元組流（MemoryStream）載入組件與 PDB 符號檔，Windows 實體檔案 100% 保持非鎖定狀態，外部工具（如 `dotnet build`、`CsviStudio`）可隨時覆寫覆蓋。
- **`PluginLoadManager`**：全域管理外掛實例，監控檔案 `LastWriteTimeUtc`，當檢測到外掛重新編譯時，自動觸發舊上下文的 `Unload()`，並無縫載入最新版本。
- **`CsviPackage.Unload()`**：套件自帶 `Unload()` 介面，當 UserPanel 面板切換或關閉時主動釋放組件記憶體。

---

### 2.2 驅動層非阻塞看門狗 (Driver Watchdog Pattern)
為防止不良驅動拖垮機台，`IDriver` 建議在下一版重構為：
```csharp
Task ExecuteTicketAsync(DriverTicket ticket, CancellationToken ct);
```
核心調度器統一掛載逾時看門狗，超過閥值自動觸發 `DriverTicketStatus.Timeout` 並通報 E-Stop，確保現場設備操作員的人身與機台安全。
